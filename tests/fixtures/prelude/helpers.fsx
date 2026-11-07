// Test fixture: a prelude script (see load_project's `prelude`).
open System.Text

let greet (name: string) = StringBuilder("hi ").Append(name).ToString()
