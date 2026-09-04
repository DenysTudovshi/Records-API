namespace Whalebone.Records.Api.Contracts;

/// <summary>
/// The error envelope, matching the <c>multiple_error</c> schema Whalebone's own API publishes.
/// </summary>
/// <remarks>
/// <para>
/// Shape: <c>{ "message": "...", "errors": [ ... ] }</c>, served as <c>application/json</c>. Their
/// schema marks neither member required, so a failure with no parameter to blame - a 404, or a body
/// that could not be read - carries <c>message</c> alone.
/// </para>
/// <para>
/// <c>request_id</c> is the one addition. It is not in their schema, but the schema sets no
/// <c>additionalProperties: false</c>, and the value is already on the response as
/// <c>X-Request-Id</c> - the header their API returns. Repeating it in the body costs nothing and
/// survives a caller who logs the body but not the headers.
/// </para>
/// </remarks>
public sealed record ErrorResponse(
    string Message,
    IReadOnlyList<ErrorDetail>? Errors,
    string RequestId)
{
    /// <summary>An envelope with no parameter to blame: a 404, or a request that could not be read.</summary>
    public static ErrorResponse Plain(string message, string requestId) => new(message, Errors: null, requestId);
}

/// <summary>
/// A whole-request failure, matching the bare <c>error</c> schema - flat, not wrapped.
/// </summary>
/// <remarks>
/// The vendor uses two error shapes, and the split is principled rather than accidental: a
/// <c>400</c> can fail on several parameters at once and gets <see cref="ErrorResponse"/>'s array,
/// while a <c>500</c> or <c>503</c> is one failure with no parameter to name and gets this. Every
/// one of their twelve operations pins <c>500</c> and <c>503</c> to the bare schema and <c>400</c>
/// to the envelope, so following them here means following both.
/// </remarks>
public sealed record FaultResponse(
    string Error,
    int ErrorCode,
    string Message,
    string RequestId)
{
    /// <summary>Something failed that is not the caller's fault, and that they cannot act on.</summary>
    public static FaultResponse Unexpected(string message, string requestId) =>
        new(ErrorCodes.UnexpectedError, ErrorCodes.UnexpectedErrorCode, message, requestId);
}

/// <summary>
/// One entry of <see cref="ErrorResponse.Errors"/>, matching their <c>error</c> schema.
/// </summary>
/// <remarks>
/// Their schema requires <c>error</c>, <c>error_code</c> and <c>message</c>, and describes
/// <c>parameter</c>, <c>value</c> and <c>accepted_values</c> as required when <c>error</c> is
/// <c>INVALID_PARAM_VALUE</c>.
/// <para>
/// <c>value</c> is deliberately absent from this type. On this service the rejected value is a
/// name, an email address or a date of birth, and an error body is among the least controlled
/// things a service emits - it reaches the caller, then their logs, then a screenshot in a ticket.
/// Their schema's <c>required</c> list is <c>[error, error_code, message]</c>, so omitting it stays
/// schema-valid; what it departs from is the prose. <c>accepted_values</c> is absent for a duller
/// reason: no field in this contract is an enum.
/// </para>
/// </remarks>
public sealed record ErrorDetail(
    string Error,
    int ErrorCode,
    string Message,
    string? Parameter)
{
    /// <summary>A required field the caller did not send.</summary>
    public static ErrorDetail Missing(string parameter, string message) =>
        new(ErrorCodes.MissingParamValue, ErrorCodes.MissingParamValueCode, message, parameter);

    /// <summary>A field the caller sent, in a form this service cannot accept.</summary>
    public static ErrorDetail Invalid(string parameter, string message) =>
        new(ErrorCodes.InvalidParamValue, ErrorCodes.InvalidParamValueCode, message, parameter);
}

/// <summary>
/// The <c>error</c> enum and its numeric codes, as published in Whalebone's OpenAPI document.
/// </summary>
/// <remarks>
/// The enum is declared in their schema; the codes appear only in their response examples, paired
/// consistently across all 47 occurrences. <c>SERVICE_UNAVAILABLE</c> is theirs and is listed for
/// completeness - this service has no path that emits it.
/// </remarks>
internal static class ErrorCodes
{
    internal const string UnexpectedError = "UNEXPECTED_ERROR";
    internal const int UnexpectedErrorCode = 10;

    internal const string InvalidParamValue = "INVALID_PARAM_VALUE";
    internal const int InvalidParamValueCode = 21;

    internal const string MissingParamValue = "MISSING_PARAM_VALUE";
    internal const int MissingParamValueCode = 22;
}
