using Xunit;
using System.Net.Http.Json;

public class AdmissionApiTests
{
    private readonly HttpClient client = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:8080") };
    [Fact] public async Task ClassesEndpointReturnsSuccess() => Assert.True((await client.GetAsync("/classes")).IsSuccessStatusCode);
    [Fact] public async Task InvalidStudentHeaderIsRejected()
    {
        var response = await client.PostAsync("/classes/not-a-class/holds", new StringContent("{}"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact] public async Task ConcurrentHoldsNeverExceedCapacity()
    {
        var create = await client.PostAsJsonAsync("/classes", new { name = "Concurrency " + Guid.NewGuid(), totalSeats = 3 });
        var data = await create.Content.ReadFromJsonAsync<ClassDto>();
        var requests = Enumerable.Range(0, 20).Select(i => { var r = new HttpRequestMessage(HttpMethod.Post, $"/classes/{data!.id}/holds"); r.Headers.Add("X-Student-Id", (20000000000000000L + i).ToString()); r.Content = new StringContent("{}"); return client.SendAsync(r); });
        var responses = await Task.WhenAll(requests);
        Assert.Equal(3, responses.Count(r => r.StatusCode == System.Net.HttpStatusCode.Created));
    }
    private record ClassDto(string id);
}
