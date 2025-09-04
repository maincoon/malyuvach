using System.Text.Json;
using System.Text.Json.Nodes;

namespace Malyuvach.Utilities;

public static class JsonHelper
{
    /// <summary>
    /// Sets the value of a specified key in a JSON object.
    /// </summary>
    /// <typeparam name="T">The type of the value to set.</typeparam>
    /// <param name="objValue">The JSON object node where the value will be set.</param>
    /// <param name="key">The key whose value needs to be set. Nested keys can be specified using dot notation.</param>
    /// <param name="value">The value to set for the specified key.</param>
    /// <param name="jsonOptions">JSON serializer options to use when serializing the value.</param>
    public static void SetJsonObjectValue<T>(
        JsonNode? objValue,
        string key,
        T value,
        JsonSerializerOptions jsonOptions)
    {
        if (objValue == null) return;

        var keys = key.Split('.');
        var currentNode = objValue;

        for (var i = 0; i < keys.Length - 1; i++)
        {
            currentNode = currentNode[keys[i]];
            if (currentNode == null) return;
        }

        // Handle different types appropriately
        JsonNode? newValue = value switch
        {
            string str => JsonValue.Create(str),
            int num => JsonValue.Create(num),
            long num => JsonValue.Create(num),
            float num => JsonValue.Create(num),
            double num => JsonValue.Create(num),
            bool boolean => JsonValue.Create(boolean),
            _ => JsonSerializer.SerializeToNode(value, jsonOptions)
        };

        currentNode[keys[^1]] = newValue;
    }

    /// <summary>
    /// Sets multiple JSON object values using the same value.
    /// </summary>
    public static void SetJsonObjectValues<T>(
        JsonNode? objValue,
        IEnumerable<string>? keys,
        T value,
        JsonSerializerOptions jsonOptions)
    {
        if (objValue == null || keys == null) return;

        foreach (var key in keys)
        {
            SetJsonObjectValue(objValue, key, value, jsonOptions);
        }
    }
}