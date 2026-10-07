using System.Text;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 2.4: the HTTP Functions are thin wrappers over the endpoint handlers. These tests call the
/// Function methods directly with a plain <see cref="DefaultHttpContext"/> — they pin the adapter, the
/// body reading and the wiring to the handlers. What they cannot prove (route precedence, middleware
/// order, the real ASP.NET Core integration) is left to the live run in the Functions container.
/// </summary>
public class FunctionsHttpTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;
    private static readonly ILoggerFactory Loggers = NullLoggerFactory.Instance;

    private static GreedyNoseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DefaultHttpContext NewContext(string? body = null, string? contentType = "application/json", string query = "")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = contentType;
        }

        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static async Task<(int Status, string Body, HttpContext Context)> RunAsync(IActionResult result, HttpContext context)
    {
        await result.ExecuteResultAsync(new ActionContext { HttpContext = context });
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, text, context);
    }

    // --- the IResult -> IActionResult adapter ----------------------------------------------------

    [Fact]
    public async Task The_adapter_runs_Content_results()
    {
        var (status, body, _) = await RunAsync(
            Results.Content("<p>hi</p>", "text/html", statusCode: 418).ToActionResult(), NewContext());

        Assert.Equal(418, status);
        Assert.Equal("<p>hi</p>", body);
    }

    [Fact]
    public async Task The_adapter_runs_Redirect_and_NoContent_results()
    {
        var redirect = await RunAsync(Results.Redirect("https://bank.example/x").ToActionResult(), NewContext());
        Assert.Equal(StatusCodes.Status302Found, redirect.Status);
        Assert.Equal("https://bank.example/x", redirect.Context.Response.Headers.Location.ToString());

        var noContent = await RunAsync(Results.NoContent().ToActionResult(), NewContext());
        Assert.Equal(StatusCodes.Status204NoContent, noContent.Status);
    }

    [Fact]
    public async Task The_adapter_runs_ValidationProblem_as_400_json()
    {
        var (status, body, context) = await RunAsync(
            Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["bad"] }).ToActionResult(), NewContext());

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("bad", body);
        Assert.Contains("json", context.Response.ContentType);
    }

    [Fact]
    public async Task The_adapter_keeps_the_BrowserResult_headers()
    {
        var (status, _, context) = await RunAsync(
            new BrowserResult(Results.Content("x", "text/html")).ToActionResult(), NewContext());

        Assert.Equal(200, status);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }

    // --- body reading -----------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_body_reads_as_null_and_the_handler_answers_400()
    {
        var function = new DeviceTokenFunction(NewDb(), TestUsers.Owner, Clock, Loggers);
        var context = NewContext();

        var (status, body, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(400, status);
        Assert.Contains("token", body);
    }

    [Fact]
    public async Task Broken_json_is_400()
    {
        var function = new DeviceTokenFunction(NewDb(), TestUsers.Owner, Clock, Loggers);
        var context = NewContext("{not json");

        var (status, _, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_415()
    {
        var function = new DeviceTokenFunction(NewDb(), TestUsers.Owner, Clock, Loggers);
        var context = NewContext("token=abc", "text/plain");

        var (status, _, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(415, status);
    }

    [Fact]
    public async Task The_device_token_body_cap_is_enforced_with_413()
    {
        var function = new DeviceTokenFunction(NewDb(), TestUsers.Owner, Clock, Loggers);
        var body = "{\"token\":\"" + new string('a', DeviceTokenEndpointMax()) + "\"}";
        var context = NewContext(body);

        var (status, _, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(413, status);
    }

    [Fact]
    public async Task The_cap_holds_when_the_length_header_is_missing()
    {
        var function = new DeviceTokenFunction(NewDb(), TestUsers.Owner, Clock, Loggers);
        var context = NewContext("{\"token\":\"" + new string('a', DeviceTokenEndpointMax()) + "\"}");
        context.Request.ContentLength = null; // chunked upload

        var (status, _, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(413, status);
    }

    private static int DeviceTokenEndpointMax() => GreedyNose.Api.Notifications.DeviceTokenEndpoint.MaxBodyBytes;

    // --- wrappers reach their handlers -----------------------------------------------------------

    [Fact]
    public async Task Rules_with_an_invalid_body_gets_the_handlers_400()
    {
        var function = new RulesFunction(NewDb(), TestUsers.Owner, Clock, Loggers);
        var context = NewContext("{}");

        var (status, _, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Debits_answers_200_with_the_stored_shape()
    {
        var function = new DebitsFunction(TestUsers.Owner, NewDb());
        var context = NewContext();

        var (status, body, _) = await RunAsync(await function.Run(context.Request, CancellationToken.None), context);

        Assert.Equal(200, status);
        Assert.Contains("\"debits\"", body);
        Assert.Contains("\"payees\"", body);
    }

    [Fact]
    public async Task Health_answers_ok_with_the_consent_state()
    {
        var consent = new ConsentStore(new FakeConsentPersistence(), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        var function = new HealthFunction(consent, new EnableBankingOptions(), Clock);
        var context = NewContext();

        var (status, body, _) = await RunAsync(function.Run(context.Request), context);

        Assert.Equal(200, status);
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"state\":\"none\"", body);
    }

    [Fact]
    public async Task An_unknown_route_answers_404()
    {
        var context = NewContext();

        var (status, _, _) = await RunAsync(new UnknownRouteFunction().Run(context.Request), context);

        Assert.Equal(404, status);
    }

    [Theory]
    [InlineData("?token=abc", "abc")]
    [InlineData("", null)]
    [InlineData("?token=", "")]
    [InlineData("?token=a&token=b", "a,b")]
    public void Query_values_are_read_like_the_minimal_api_binder_did(string query, string? expected)
    {
        var context = NewContext(query: query);

        Assert.Equal(expected, context.Request.Query.Text("token"));
    }
}
