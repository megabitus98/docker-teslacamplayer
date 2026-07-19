using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace TeslaCamPlayer.BlazorHosted.Client.Helpers;

public static class HttpClientNewtonsoftJsonHelper
{
    // ponytail: camelCase serialization keeps request wire bytes identical to the
    // System.Text.Json web defaults (PostAsJsonAsync) these helpers replace.
    private static readonly JsonSerializerSettings CamelCaseSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static async Task<TValue> GetFromNewtonsoftJsonAsync<TValue>(
        this HttpClient client,
        [StringSyntax(StringSyntaxAttribute.Uri)] string requestUri) =>
        JsonConvert.DeserializeObject<TValue>(await client.GetStringAsync(requestUri));

    public static Task<HttpResponseMessage> PostAsNewtonsoftJsonAsync<TValue>(
        this HttpClient client,
        [StringSyntax(StringSyntaxAttribute.Uri)] string requestUri,
        TValue value) =>
        client.PostAsync(requestUri, new StringContent(
            JsonConvert.SerializeObject(value, CamelCaseSettings), Encoding.UTF8, "application/json"));

    public static async Task<TValue> ReadFromNewtonsoftJsonAsync<TValue>(this HttpResponseMessage response) =>
        JsonConvert.DeserializeObject<TValue>(await response.Content.ReadAsStringAsync());
}
