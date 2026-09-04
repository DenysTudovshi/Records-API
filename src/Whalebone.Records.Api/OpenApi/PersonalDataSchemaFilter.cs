using System.Reflection;
using System.Text.Json;

using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

using Swashbuckle.AspNetCore.SwaggerGen;

using Whalebone.Records.Application.Abstractions;

namespace Whalebone.Records.Api.OpenApi;

/// <summary>
/// Publishes <see cref="PersonalDataAttribute"/> onto the OpenAPI document as
/// <c>x-wb-encrypt: true</c>, the extension Whalebone use to mark personal data on their own
/// contracts.
/// </summary>
/// <remarks>
/// <para>
/// One difference from theirs, stated because it is visible to anyone who compares the documents:
/// they annotate query <em>parameters</em>, this annotates schema <em>properties</em>, because that
/// is where this service's personal data lives. Same extension, same meaning, the position that the
/// respective contracts allow.
/// </para>
/// <para>
/// It declares a fact, not a behaviour - nothing downstream reads it. What it is for is that a
/// reader of the document, or a gateway that already understands the extension, learns which three
/// of the four fields are personal without being told in prose.
/// </para>
/// </remarks>
internal sealed class PersonalDataSchemaFilter : ISchemaFilter
{
    internal const string Extension = "x-wb-encrypt";

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (schema.Properties is null || schema.Properties.Count == 0)
        {
            return;
        }

        foreach (var property in context.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<PersonalDataAttribute>() is null)
            {
                continue;
            }

            // The document is generated under the same snake_case policy the wire uses, so the
            // schema key is the converted name rather than the CLR one.
            var wireName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);

            if (schema.Properties.TryGetValue(wireName, out var propertySchema))
            {
                propertySchema.Extensions[Extension] = new OpenApiBoolean(true);
            }
        }
    }
}
