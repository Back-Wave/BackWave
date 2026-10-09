; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; BW0015 was in an earlier design of the complex payload members and never shipped. The ID stays unused.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
BW0011  | BackWave | Error    | System.Text.Json attribute on a delegated payload member
BW0012  | BackWave | Error    | Type is listed in more than one JsonSerializerContext
BW0013  | BackWave | Error    | JsonSerializerContext listing cannot serve the generated codec
BW0014  | BackWave | Error    | Generic [Job] type
BW0016  | BackWave | Error    | [JsonConverter] on a [Job] payload with delegated members
BW0017  | BackWave | Error    | Type the job codec needs is not listed in any JsonSerializerContext
BW0018  | BackWave | Warning  | System.Text.Json attribute on a scalar payload member
BW0019  | BackWave | Warning  | [JsonConverter] on a [Job] payload with only scalar members
