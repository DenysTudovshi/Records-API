namespace Whalebone.Records.Application.Abstractions;

/// <summary>
/// Why a value failed validation, in the only two flavours a caller can act on differently.
/// </summary>
/// <remarks>
/// Carried on <c>ValidationFailure.ErrorCode</c> and translated at the edge into whatever the wire
/// contract calls them. Deliberately neutral: this project knows nothing about HTTP or about any
/// particular error envelope, and naming the vocabulary after one would point the dependency arrow
/// the wrong way.
/// </remarks>
public static class ValidationErrorCodes
{
    /// <summary>The caller did not send the field at all.</summary>
    public const string Missing = "missing";

    /// <summary>The caller sent the field, in a form this service cannot accept.</summary>
    public const string Invalid = "invalid";
}
