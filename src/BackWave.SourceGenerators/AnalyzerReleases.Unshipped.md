; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
BW0001  | BackWave | Error    | Wire Name is missing
BW0002  | BackWave | Error    | Duplicate Wire Name
BW0003  | BackWave | Error    | No handler for [Job] type
BW0004  | BackWave | Error    | Unsupported payload member type
BW0005  | BackWave | Error    | Invalid [Job] method shape
BW0006  | BackWave | Error    | Duplicate generated job type
BW0007  | BackWave | Error    | Workflow type is not listed in any JsonSerializerContext
BW0008  | BackWave | Error    | Invalid [Retry] attempt ceiling
BW0009  | BackWave | Error    | Invalid [Retry] backoff intervals
BW0010  | BackWave | Error    | [Retry] with no [Job]
