// The single place namespaces are declared for this project.
// ImplicitUsings is disabled and MSBuild <Using> items are not used, so if a
// namespace is available in a source file, it is because it appears here.
//
// Namespaces are added as the code comes to need them. IDE0005 is an error, so
// an entry nothing uses fails the build rather than lingering as clutter.
// Do not repeat any of these in a source file (CS8933).

global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Net.Http;
global using System.Runtime.CompilerServices;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Text.Json.Serialization.Metadata;
global using System.Threading;
global using System.Threading.Tasks;
