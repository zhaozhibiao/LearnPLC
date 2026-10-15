using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StWorkbench.Mcp;

// Explicit structural schema avoids exporter interpreting optional record-struct defaults as null.
// Semantic constraints remain enforced by the existing engineering validators.
public static class EngineeringSchemas
{
    public static JsonObject For(Type type)
    {
        var definitions = new JsonObject();
        JsonNode Node(Type item)
        {
            if (Nullable.GetUnderlyingType(item) is { } nullable)
                return new JsonObject { ["anyOf"] = new JsonArray(Node(nullable), new JsonObject { ["type"] = "null" }) };
            if (item == typeof(string) || item == typeof(Guid)) return new JsonObject { ["type"] = "string" };
            if (item == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
            if (item.IsEnum) return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(item).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
            if (item.IsPrimitive || item == typeof(decimal)) return new JsonObject { ["type"] = item == typeof(float) || item == typeof(double) || item == typeof(decimal) ? "number" : "integer" };
            if (typeof(IEnumerable).IsAssignableFrom(item))
            {
                var element = item.IsArray ? item.GetElementType()! : item.GetGenericArguments().Single();
                return new JsonObject { ["type"] = "array", ["items"] = Node(element) };
            }
            var key = item.Name;
            if (!definitions.ContainsKey(key))
            {
                var shape = new JsonObject { ["type"] = "object" }; definitions[key] = shape;
                var properties = new JsonObject();
                foreach (var property in item.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always))
                    properties[property.Name] = Node(property.PropertyType);
                shape["properties"] = properties; shape["additionalProperties"] = false;
            }
            return new JsonObject { ["$ref"] = "#/$defs/" + key };
        }
        var schema = (JsonObject)Node(type);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$defs"] = definitions;
        return schema;
    }
}
