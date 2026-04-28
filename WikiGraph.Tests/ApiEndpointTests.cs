using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WikiGraph.Api.Application.Services;
using WikiGraph.Api.Configuration;
using WikiGraph.Api.Controllers;
using WikiGraph.Api.Infrastructure.Persistence;
using WikiGraph.Api.Infrastructure.Wikipedia;
using System.Net.Http;
using System.Text;
using WikiGraph.Contracts;
using System.Text.Json;
using Xunit;

namespace WikiGraph.Tests;

public class ApiEndpointTests
{

[Fact]

public async Task TestAPIUp(){ //tests if API is up
    var client = new HttpClient();
    var response = await client.GetAsync("http://localhost:5052/api/health");
    string content;
    Assert.True(response.IsSuccessStatusCode);
        if (response.IsSuccessStatusCode)
        {
             content = await response.Content.ReadAsStringAsync();
             Assert.Equal("{\"status\":\"ok\"}",content);
        }
    }

private async Task TestSessionCreation(){ //tests if API is up
    var client = new HttpClient();
    var json = "{\"title\":\"TestingYouShouldNotSeeThis\"}";
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    var response = await client.PostAsync("http://localhost:5052/api/sessions",content);
    Assert.True(response.IsSuccessStatusCode);
}



#pragma warning disable CS8602
#pragma warning disable CS8600
private async Task<string> TestSessionList(){ //tests if API for session list works
    var client = new HttpClient();
    string sID="";
    var response = await client.GetAsync("http://localhost:5052/api/sessions");
    Assert.True(response.IsSuccessStatusCode);
        string content = await response.Content.ReadAsStringAsync();


var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };


    List<SessionSummary> sessions = JsonSerializer.Deserialize<List<SessionSummary>>(content, options);

    
    foreach(var s in sessions){ // looks through and delets all testing functions
        if(s.Title == "TestingYouShouldNotSeeThis")
            {
                        sID=s.SessionId;
            }
    }

    if(sID == ""){
        Assert.Fail("Could not find sessionID.");
    }

return sID;
}




private async Task TestSessionDelete(string sID){ //tests if API for deleting all sessions created by Test Session Creation
    var client = new HttpClient();
    var responseDel = await client.DeleteAsync($"http://localhost:5052/api/sessions/{sID}");
    Assert.True(responseDel.IsSuccessStatusCode);

}
#pragma warning disable CS8600
#pragma warning restore CS8602






private async Task TestCreateArticle(string sID){ //Creates article 
    var client = new HttpClient();
    var json = "{\"topic\":\"Test\",\"wikipediaUrl\":\"Test\"}";
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    var response = await client.PostAsync($"http://localhost:5052/api/sessions/{sID}/articles",content);
    Assert.True(response.IsSuccessStatusCode);
}

private async Task TestRetriveSession(string sID){ //tests if API for session list works
    var client = new HttpClient();
    var response = await client.GetAsync($"http://localhost:5052/api/sessions/{sID}");
    Assert.True(response.IsSuccessStatusCode);
}

[Fact]
public async Task Test()
{
    
    await TestSessionCreation(); // Check if Session is created
    string sID =await TestSessionList(); // Check if Session is in the list and returns if it is. 
    await TestCreateArticle(sID);
    await TestRetriveSession(sID);
    await TestSessionDelete(sID); // Check if can destroy session
}

}
