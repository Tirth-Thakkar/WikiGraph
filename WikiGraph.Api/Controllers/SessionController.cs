using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using WikiGraph.Api.Application.Services;
using WikiGraph.Api.Infrastructure.Persistence;
using WikiGraph.Contracts;

namespace WikiGraph.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionController : ControllerBase
{
    private readonly SqliteSessionRepository _sessionRepository;
    private readonly WikiSessionService _wikiSessionService;

    // Wires the session repository and article workflow into the HTTP layer.
    public SessionController(SqliteSessionRepository sessionRepository, WikiSessionService wikiSessionService)
    {
        _sessionRepository = sessionRepository;
        _wikiSessionService = wikiSessionService;
    }

    // Returns the current session list for the active user.
    [HttpGet]
    public ActionResult<IReadOnlyList<SessionSummary>> GetSessions() => Ok(_sessionRepository.GetSessions());

    // Creates a session, using a default title when none is provided.
    [HttpPost]
    public ActionResult<SessionSummary> CreateSession([FromBody] CreateSessionRequest? input)
    {
        var title = input?.Title is { Length: > 0 } ? input.Title : "New session";
        var session = _sessionRepository.CreateSession(title);
        return Created($"/api/sessions/{session.SessionId}", session);
    }



    // Adds a Wikipedia topic or URL to the session and returns the updated session detail.
    [HttpPost("{sessionId}/articles")]
    public async Task<ActionResult<SessionDetailDto>> AddArticle(
        string sessionId,
        [FromBody] AddWikiArticleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { error = "SessionId is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Topic) && string.IsNullOrWhiteSpace(request.WikipediaUrl))
        {
            return BadRequest(new { error = "Topic or WikipediaUrl is required." });
        }

        return Ok(await _wikiSessionService.AddArticleAsync(sessionId, request, cancellationToken));
    }

    [HttpDelete("{sessionId}")]
    public IActionResult Delete(string sessionId, CancellationToken cancellationToken)
    {   
        _sessionRepository.DeleteSession(sessionId);
        return Ok(new {status= $"{sessionId} Deleted"});
    }

    // Returns one session with its messages, citations, and graphs.
    [HttpGet("{sessionId}")]
    public ActionResult<SessionDetailDto> GetSession(string sessionId)
    {
        return _sessionRepository.GetSession(sessionId) is { } session ? Ok(session) : NotFound();
    }

    // Returns the stored graphs for one session.
    [HttpGet("{sessionId}/graphs")]
    public ActionResult<IReadOnlyList<GraphDto>> GetGraphs(string sessionId)
    {
        return _sessionRepository.GetGraphs(sessionId) is { } graphs ? Ok(graphs) : NotFound();
    }

  
}
