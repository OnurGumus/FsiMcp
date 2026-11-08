module HostShadowLib.Lib

open Microsoft.Extensions.Logging

let level = LogLevel.Warning

/// ILoggingBuilder is in Logging.Abstractions only since 8.0: this runs only on a recent copy.
let builderAssemblyMajor () = typeof<ILoggingBuilder>.Assembly.GetName().Version.Major
