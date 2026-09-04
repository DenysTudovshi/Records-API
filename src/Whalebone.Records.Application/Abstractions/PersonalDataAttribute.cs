namespace Whalebone.Records.Application.Abstractions;

/// <summary>
/// Marks a contract member as personal data.
/// </summary>
/// <remarks>
/// A classification, not a mechanism - nothing here encrypts or redacts anything. What it buys is
/// that the classification sits on the field rather than in a document, so the OpenAPI filter that
/// publishes it and anyone reading the type are looking at the same list. Whalebone annotate the
/// same fact on their own parameters, with <c>x-wb-encrypt</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalDataAttribute : Attribute;
