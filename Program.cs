using AzChatGptSms;
using Azure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Azure;
using Twilio.AspNet.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.Configure<ForwardedHeadersOptions>(
    options => options.ForwardedHeaders = ForwardedHeaders.All
);

builder.Services
    .AddTwilioClient()
    .AddTwilioRequestValidation();

builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.AddOpenAIClient(
        new Uri(builder.Configuration["OpenAI:Endpoint"]),
        new AzureKeyCredential(builder.Configuration["OpenAI:ApiKey"])
    );
});

var app = builder.Build();

app.UseSession();

app.UseForwardedHeaders();

app.UseTwilioRequestValidation();

app.MapMessageEndpoint();

app.Run();