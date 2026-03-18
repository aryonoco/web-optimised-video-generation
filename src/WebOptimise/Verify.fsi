namespace WebOptimise

open System
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module Verify =

    val internal validateVideoProfile: json: string -> VerificationIssue voption
    val internal validateFaststart: stderr: string -> VerificationIssue voption
    val internal validateCuesFront: data: ReadOnlySpan<byte> -> VerificationIssue voption
    val internal parseKeyframeTimes: output: string -> float list
    val internal analyseKeyframeIntervals: output: string -> VerificationIssue voption

    val verifyEncoded: env: Env -> path: OutputPath -> Task<Result<unit, VerificationIssue list>>
    val verifyRemuxed: env: Env -> path: OutputPath -> Task<Result<unit, VerificationIssue list>>
    val verifyWebm: env: Env -> path: OutputPath -> Task<Result<unit, VerificationIssue list>>
