module SampleLib.Lib

open Newtonsoft.Json

let version = 1

/// Exercises a NuGet dependency at runtime.
let roundTrip (xs: int[]) : int[] = JsonConvert.DeserializeObject<int[]>(JsonConvert.SerializeObject xs)
