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
    [Fact] public async Task ConfirmingAnotherStudentsHoldIsForbidden()
    {
        var create = await client.PostAsJsonAsync("/classes", new { name = "Ownership " + Guid.NewGuid(), totalSeats = 1 });
        var data = await create.Content.ReadFromJsonAsync<ClassDto>();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/classes/{data!.id}/holds");
        request.Headers.Add("X-Student-Id", "20000000000000001");
        var hold = await client.SendAsync(request);
        var holdData = await hold.Content.ReadFromJsonAsync<HoldDto>();
        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/holds/{holdData!.holdId}/confirm");
        confirm.Headers.Add("X-Student-Id", "20000000000000002");
        var response = await client.SendAsync(confirm);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact] public async Task CancellingUnknownHoldReturnsNotFound()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/holds/does-not-exist");
        request.Headers.Add("X-Student-Id", "20000000000000001");
        var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
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
    private record HoldDto(string holdId);
}
