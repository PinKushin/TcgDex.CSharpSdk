#!/usr/bin/env bash
#
# Run one long measurement workload on an Oracle measurement box.
#
#   bash build/run-measurements.sh fuzz [seconds]  [--no-pull]
#   bash build/run-measurements.sh stryker         [--no-pull]
#
# Lives in the repo rather than only on the box so it is backed up, reviewable, and
# changed through the same PR flow as everything else.
#
# One workload at a time, on purpose. The box serialises on a shared lock because a
# competing build does not merely slow a measurement down — it CORRUPTS it. A build
# failure caused by a neighbour is scored by Stryker as a SURVIVING MUTANT, which
# arrives as a plausible number rather than as an error.
set -euo pipefail

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"

# MSBuild node reuse leaves daemons alive for ~15 minutes after the build that started
# them. On a box whose entire job is one workload at a time they are pure downside: they
# hold obj/, they hold memory, and they inherit fd 9 — which keeps the lock held long
# after this script exits. Every long command below also closes it explicitly with `9>&-`.
export MSBUILDDISABLENODEREUSE=1

# Written into every run directory as `.owner`. The prune at the bottom deletes ONLY
# directories carrying this exact marker: ~/measurements is shared, and a name glob is
# not safe here — `*-fuzz/` would match a neighbour's `<stamp>-<sha>-tf2-fuzz`.
OWNER="tcgdex-csharpsdk"

WORKDIR="${TCGDEX_DIR:-$HOME/tcgdex}"

# BOX-WIDE, and deliberately NOT named after this project. The thing being serialised is
# the machine. A per-project lock name looks like tidy namespacing and is the worst
# available mistake: both projects take their own lock, both run, and the collision is
# reported as a plausible score rather than an error.
#
# NEVER `rm` this file, not even as cleanup. Deleting it does not free the lock — flock is
# on the open file description — but it DOES unlink the inode, so the next run creates a
# fresh file and takes a fresh lock while the previous holder is still working. That is
# not a stuck lock, it is NO lock.
LOCK="/tmp/measurement-box.lock"

# The corpus is shared with the GitHub runner through a release asset. Reading is
# unauthenticated because the repo is public; writing needs the token below.
GH_REPO="PinKushin/TcgDex.CSharpSdk"
CORPUS_TAG="fuzz-corpus"
SHARED_CORPUS_URL="https://github.com/${GH_REPO}/releases/download/${CORPUS_TAG}/corpus.tar.zst"
TOKEN_FILE="${HOME}/.tcgdex-gh-token"

# Pack a corpus directory and replace the release asset with it.
#
# The upload endpoint REFUSES a duplicate asset name rather than replacing it, so
# the old one is deleted first. Every step's HTTP status is checked: a failed
# publish that looked like a success would leave both machines quietly diverging,
# which is the whole problem this is meant to solve.
publish_corpus() {
  local dir="$1" id old code cfg
  # The token goes in a curl config file rather than on the command line: an
  # argument is visible in `ps` to every process on the box, and several projects
  # share this machine as the same user. Trap-cleaned so a failure cannot leave it.
  cfg=$(mktemp)
  chmod 600 "$cfg"
  trap 'rm -f "$cfg"' RETURN
  printf 'header = "Authorization: Bearer %s"\n' "$(cat "$TOKEN_FILE")" > "$cfg"
  local auth=(--config "$cfg" -H "Accept: application/vnd.github+json")

  tar -C "$dir" -cf - . | zstd -19 -T0 -q -o /tmp/corpus-publish.tar.zst -f

  id=$(curl -fsS "${auth[@]}" \
    "https://api.github.com/repos/${GH_REPO}/releases/tags/${CORPUS_TAG}" | jq -r '.id // empty')
  [ -n "$id" ] || { echo "    publish: release ${CORPUS_TAG} not found" >&2; return 1; }

  old=$(curl -fsS "${auth[@]}" \
    "https://api.github.com/repos/${GH_REPO}/releases/${id}/assets" \
    | jq -r '.[]|select(.name=="corpus.tar.zst")|.id')
  [ -n "$old" ] && curl -fsS -X DELETE "${auth[@]}" \
    "https://api.github.com/repos/${GH_REPO}/releases/assets/${old}" >/dev/null

  code=$(curl -s -o /tmp/corpus-publish.json -w '%{http_code}' -X POST "${auth[@]}" \
    -H "Content-Type: application/zstd" --data-binary @/tmp/corpus-publish.tar.zst \
    "https://uploads.github.com/repos/${GH_REPO}/releases/${id}/assets?name=corpus.tar.zst")
  rm -f /tmp/corpus-publish.tar.zst

  if [ "$code" != "201" ]; then
    echo "    publish: HTTP ${code} -- $(jq -r '.message // "no message"' /tmp/corpus-publish.json)" >&2
    return 1
  fi
  echo "    published $(ls "$dir" | wc -l) inputs to the ${CORPUS_TAG} release"
}

MODE="${1:-}"
shift || true

PULL=1
ARG=""
for a in "$@"; do
  case "$a" in
    --no-pull) PULL=0 ;;
    *) ARG="$a" ;;
  esac
done

if [ -z "$MODE" ]; then
  echo "usage: $0 {fuzz [seconds]|stryker} [--no-pull]" >&2
  exit 2
fi

exec 9>"$LOCK"
if ! flock -n 9; then
  echo "ERROR: another measurement run holds $LOCK. One at a time." >&2
  # Name the holder. Without this a refusal is indistinguishable from a stale lock, and
  # the tempting fix — deleting the file — does nothing, because flock is on the open fd
  # rather than the path. Killing the PID is what frees it.
  command -v fuser >/dev/null && { echo "held by:" >&2; fuser -v "$LOCK" >&2 2>&1 || true; }
  exit 1
fi

cd "$WORKDIR"

# NOTE: a runner that pulls itself takes effect ONE RUN LATE. `git reset --hard` replaces
# this file by rename, so the running bash keeps reading the old inode and finishes the
# old body — while the banner below prints the NEW sha, because that is read after the
# pull. Verify a change to this script on its SECOND run, or with --no-pull.
if [ "$PULL" -eq 1 ]; then
  git fetch --quiet origin
  git reset --quiet --hard origin/main
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
SHA="$(git rev-parse --short HEAD)"
OUT="$HOME/measurements/${STAMP}-${SHA}-tcgdex-${MODE}"
mkdir -p "$OUT"
printf '%s\n' "$OWNER" > "${OUT}/.owner"

echo "==> tcgdex ${MODE} on ${SHA} at $(date '+%F %H:%M:%S %Z'), output -> ${OUT}"

case "$MODE" in
  fuzz)
    SECONDS_BUDGET="${ARG:-900}"

    FUZZ_OUT="${HOME}/tcgdex-fuzz-out"
    CORPUS="${HOME}/tcgdex-corpus"
    FINDINGS="${HOME}/tcgdex-findings"

    rm -rf "$FUZZ_OUT"
    mkdir -p "$CORPUS" "$FINDINGS"

    dotnet publish TcgDex.CSharpSdk.Fuzz -c Release -o "$FUZZ_OUT" --nologo -v q 9>&-

    # Instrument the SDK, not the harness: coverage feedback has to come from the code
    # under test or the fuzzer cannot make progress.
    sharpfuzz "${FUZZ_OUT}/TcgDex.CSharpSdk.dll" 9>&-

    # Seed once per mode byte. The harness multiplexes on the first input byte, so a
    # fixture's own first byte would otherwise decide which single mode it reaches.
    for fixture in TcgDex.CSharpSdk.Tests/Fixtures/*.json; do
      [ -f "$fixture" ] || continue
      name="$(basename "$fixture" .json)"
      for mode in 0 1 2 3 4 5 6; do
        printf "$(printf '\\%03o' "$mode")" | cat - "$fixture" > "${CORPUS}/${name}-m${mode}.bin"
      done
    done

    # Pull whatever the CI runner has found and fold it in.
    #
    # The corpus is shared between this box and the GitHub runner through a
    # release asset, because there is no filesystem between them and an Actions
    # cache cannot be read from outside a runner. Reading needs no credential --
    # the repo is public.
    #
    # The point is not speed. Both sides run the SAME inputs against DIFFERENT
    # architectures: this box is ARM64, the runner is x64. An input discovered on
    # one is currently never executed on the other, so a fault that only shows on
    # one architecture is unreachable from the machine that found the input.
    # Pooling is what makes it reachable.
    #
    # A fetch failure is not fatal. The local corpus and the seeds above still
    # make the run useful; it just starts further back.
    echo "==> fetching the shared corpus"
    if curl -fsSL -o /tmp/shared-corpus.tar.zst "$SHARED_CORPUS_URL"; then
      rm -rf /tmp/shared-corpus && mkdir -p /tmp/shared-corpus
      if tar -C /tmp/shared-corpus -xf /tmp/shared-corpus.tar.zst 2>/dev/null; then
        before=$(ls "$CORPUS" | wc -l)
        cp -n /tmp/shared-corpus/* "$CORPUS/" 2>/dev/null || true
        echo "    merged: ${before} -> $(ls "$CORPUS" | wc -l) inputs"
      else
        echo "    WARNING: the asset did not unpack; continuing with the local corpus" >&2
      fi
      rm -rf /tmp/shared-corpus /tmp/shared-corpus.tar.zst
    else
      echo "    none published yet, or fetch failed; continuing with the local corpus"
    fi

    # PROVE THE INSTRUMENT BEFORE TRUSTING THE MEASUREMENT.
    #
    # `-artifact_prefix` writes NOTHING through libfuzzer-dotnet: a managed exception
    # aborts the .NET child, the bridge dies with it, and libFuzzer's crash handler never
    # runs. So the harness preserves the input itself — and this proves that path still
    # works before spending the budget. An empty findings directory and a harness that
    # cannot record one look identical.
    SELFTEST_DIR="${HOME}/tcgdex-selftest"
    rm -rf "$SELFTEST_DIR" && mkdir -p "$SELFTEST_DIR"
    printf '\000{}' > "${SELFTEST_DIR}/input.bin"

    echo "==> self-test: proving a crashing input is preserved"
    ( cd "$SELFTEST_DIR" && TCGDEX_FUZZ_SELFTEST=1 "${HOME}/libfuzzer-dotnet" \
        --target_path="${FUZZ_OUT}/TcgDex.CSharpSdk.Fuzz" \
        -runs=1 input.bin 9>&- > "${OUT}/selftest.log" 2>&1 ) || true

    if [ "$(find "${SELFTEST_DIR}/findings" -type f 2>/dev/null | wc -l)" -eq 0 ]; then
      echo "ERROR: the self-test produced no preserved input." >&2
      echo "       A finding could not be recorded, so the whole run would be meaningless." >&2
      tail -20 "${OUT}/selftest.log" >&2
      exit 1
    fi
    echo "    ok: self-test preserved $(find "${SELFTEST_DIR}/findings" -type f | wc -l) input(s)"

    # Control. Without it, a harness that wrote a file unconditionally would pass the
    # check above and prove nothing.
    rm -rf "${SELFTEST_DIR}/findings"
    ( cd "$SELFTEST_DIR" && "${HOME}/libfuzzer-dotnet" \
        --target_path="${FUZZ_OUT}/TcgDex.CSharpSdk.Fuzz" \
        -runs=1 input.bin 9>&- >> "${OUT}/selftest.log" 2>&1 ) || true

    if [ "$(find "${SELFTEST_DIR}/findings" -type f 2>/dev/null | wc -l)" -ne 0 ]; then
      echo "ERROR: the control run also wrote a file, with the self-test flag unset." >&2
      echo "       The file seen above is therefore not evidence that anything works." >&2
      exit 1
    fi
    echo "    ok: control wrote nothing, as required"
    rm -rf "$SELFTEST_DIR"

    # -artifact_prefix stays set even though it writes nothing through this bridge, so a
    # future toolchain that does produce artifacts puts them beside ours rather than in $PWD.
    ( cd "$FINDINGS/.." && "${HOME}/libfuzzer-dotnet" \
        --target_path="${FUZZ_OUT}/TcgDex.CSharpSdk.Fuzz" \
        -max_total_time="${SECONDS_BUDGET}" \
        -artifact_prefix="${FINDINGS}/" \
        -print_final_stats=1 \
        "$CORPUS" 9>&- 2>&1 | tee "${OUT}/fuzz.log" ) || true

    grep -E "stat::|Done .* runs" "${OUT}/fuzz.log" | tail -8 || true

    escaped=$(find "$FINDINGS" -type f 2>/dev/null | wc -l)
    if [ "$escaped" -ne 0 ]; then
      cp -r "$FINDINGS" "${OUT}/findings"
      echo "FINDINGS: ${escaped} input(s) escaped the documented outcome — see ${OUT}/findings" >&2
      exit 1
    fi
    echo "no inputs escaped"

    # Keep the corpus small enough that the next run spends its budget mutating rather
    # than loading. Skipped when something escaped: that is evidence, not clutter.
    MIN="${CORPUS}-min"
    rm -rf "$MIN" && mkdir -p "$MIN"
    before=$(ls "$CORPUS" | wc -l)
    "${HOME}/libfuzzer-dotnet" \
      --target_path="${FUZZ_OUT}/TcgDex.CSharpSdk.Fuzz" \
      -merge=1 "$MIN" "$CORPUS" 9>&- > "${OUT}/merge.log" 2>&1 || true
    if [ "$(ls "$MIN" | wc -l)" -gt 0 ]; then
      rm -rf "$CORPUS" && mv "$MIN" "$CORPUS"
    fi
    echo "corpus ${before} -> $(ls "$CORPUS" | wc -l)"

    # Publish the minimised result back, so the runner starts from what this box
    # learned. Only after a clean run: the escape check above exits before here,
    # so a crashing run cannot overwrite the shared corpus with a partial one.
    #
    # Writing needs a credential, unlike reading. A fine-grained token scoped to
    # this repo with Contents:write lives at $TOKEN_FILE, 0600. Absent, the run
    # still succeeds and simply does not publish -- the box keeps its own corpus
    # and nothing is lost.
    if [ -r "$TOKEN_FILE" ]; then
      publish_corpus "$CORPUS" || echo "    WARNING: publish failed; the local corpus is unaffected" >&2
    else
      echo "    no token at $TOKEN_FILE; not publishing"
    fi
    ;;

  stryker)
    dotnet tool restore 9>&-
    # --concurrency defaults to HALF the logical processors, which integer-divides to 1
    # on a 3-core box. Set here rather than in stryker-config.json, where the conservative
    # default is right for a developer's machine.
    dotnet stryker --concurrency "$(nproc)" 2>&1 9>&- | tee "${OUT}/stryker.log"
    cp -r StrykerOutput "${OUT}/" 2>/dev/null || true
    grep -E "final mutation score|Killed:|Survived:|Timeout:|NoCoverage" "${OUT}/stryker.log" | tail -8 || true
    ;;

  *)
    echo "unknown mode: $MODE" >&2
    exit 2
    ;;
esac

# Keep the newest 30 runs THIS project owns. Directories with no `.owner`, or one naming
# another project, are left alone — deleting on absence would recreate the bug this marker
# exists to prevent for anyone who has not adopted it yet.
cd "$HOME/measurements" 2>/dev/null || exit 0
for dir in $(ls -1dt */ 2>/dev/null | tail -n +31); do
  [ -f "${dir}.owner" ] || continue
  [ "$(cat "${dir}.owner" 2>/dev/null)" = "$OWNER" ] || continue
  rm -rf "$dir"
done

echo "==> done at $(date '+%F %H:%M:%S %Z')"
