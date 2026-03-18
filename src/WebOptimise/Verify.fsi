namespace WebOptimise

open System
open System.Text.Json
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module Verify =

    val internal validateVideoProfile: root: JsonElement -> Result<unit, VerificationIssue>
    val internal validateFaststart: stderr: string -> Result<unit, VerificationIssue>
    val internal validateCuesFront: data: ReadOnlySpan<byte> -> Result<unit, VerificationIssue>
    val internal parseKeyframeTimes: output: string -> float list
    val internal analyseKeyframeIntervals: output: string -> Result<unit, VerificationIssue>

    val checkFaststart: env: Env -> path: OutputPath -> Task<Result<unit, VerificationIssue list>>
    val checkCuesFront: env: Env -> path: OutputPath -> Task<Result<unit, VerificationIssue list>>
    val verifyEncoded: env: Env -> path: OutputPath -> Task<Result<unit, VerificationIssue list>>
