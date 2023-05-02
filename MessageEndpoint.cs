using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Twilio.Clients;
using Twilio.Rest.Api.V2010.Account;

namespace AzChatGptSms;

public static class MessageEndpoint
{
    private const string PreviousMessagesKey = "PreviousMessages";

    public static IEndpointRouteBuilder MapMessageEndpoint(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/message", OnMessage);
        return builder;
    }

    private static async Task<IResult> OnMessage(
        HttpContext context,
        OpenAIClient openAiClient,
        ITwilioRestClient twilioClient,
        IConfiguration configuration,
        CancellationToken cancellationToken
    )
    {
        var request = context.Request;
        var session = context.Session;
        await session.LoadAsync(cancellationToken).ConfigureAwait(false);

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var receivedFrom = form["From"].ToString();
        var sentTo = form["To"].ToString();
        var body = form["Body"].ToString().Trim();

        // handle reset
        if (body.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            RemovePreviousMessages(session);
            await MessageResource.CreateAsync(
                to: receivedFrom,
                from: sentTo,
                body: "Your conversation is now reset.",
                client: twilioClient
            ).ConfigureAwait(false);
            return Results.Ok();
        }

        var messages = GetPreviousMessages(session);
        messages.Add(new ChatMessage(ChatRole.User, body));

        // ChatGPT doesn't need the phone number, just any string that uniquely identifies the user,
        // hence I'm hashing the phone number to not pass in PII unnecessarily
        var userId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receivedFrom)));

        string chatResponse = await GetChatResponse(
            openAiClient,
            userId,
            messages,
            configuration["OpenAI:ModelName"],
            cancellationToken
        );

        messages.Add(new ChatMessage(ChatRole.Assistant, chatResponse));
        SetPreviousMessages(session, messages);

        // 320 is the recommended message length for maximum deliverability,
        // but you can change this to your preference. The max for a Twilio message is 1600 characters.
        // https://support.twilio.com/hc/en-us/articles/360033806753-Maximum-Message-Length-with-Twilio-Programmable-Messaging
        var responseMessages = SplitTextIntoMessages(chatResponse, maxLength: 320);

        // Twilio webhook expects a response within 10 seconds.
        // we don't need to wait for the SendResponse task to complete, so don't await
        _ = SendResponse(twilioClient, to: receivedFrom, from: sentTo, responseMessages);

        return Results.Ok();
    }

    /// <summary>
    /// Splits the text into multiple strings by splitting it by its paragraphs
    /// and adding them back together until the max length is reached.
    /// Warning: This assumes each paragraph does not exceed the maxLength already, which may not be the case.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="maxLength"></param>
    /// <returns>Returns a list of messages, each not exceeding the maxLength</returns>
    private static List<string> SplitTextIntoMessages(string text, int maxLength)
    {
        List<string> messages = new();
        var paragraphs = text.Split("\n\n");

        StringBuilder messageBuilder = new();
        for (int paragraphIndex = 0; paragraphIndex < paragraphs.Length - 1; paragraphIndex++)
        {
            string currentParagraph = paragraphs[paragraphIndex];
            string nextParagraph = paragraphs[paragraphIndex + 1];
            messageBuilder.Append(currentParagraph);

            // + 2 for "\n\n"
            if (messageBuilder.Length + nextParagraph.Length > maxLength + 2)
            {
                messages.Add(messageBuilder.ToString());
                messageBuilder.Clear();
            }
            else
            {
                messageBuilder.Append("\n\n");
            }
        }

        messageBuilder.Append(paragraphs.Last());
        messages.Add(messageBuilder.ToString());

        return messages;
    }

    private static async Task<string> GetChatResponse(
        OpenAIClient openAiClient,
        string userId,
        List<ChatMessage> messages,
        string modelName,
        CancellationToken cancellationToken
    )
    {
        var chatCompletionOptions = new ChatCompletionsOptions
        {
            User = userId
        };
        foreach (var message in messages)
            chatCompletionOptions.Messages.Add(message);

        var chatCompletionsResponse = await openAiClient.GetChatCompletionsAsync(
            modelName,
            chatCompletionOptions,
            cancellationToken
        );

        return chatCompletionsResponse.Value.Choices[0].Message.Content;
    }

    private static async Task SendResponse(
        ITwilioRestClient twilioClient,
        string to,
        string from,
        List<string> responseMessages
    )
    {
        foreach (var responseMessage in responseMessages)
        {
            await MessageResource.CreateAsync(
                    to: to,
                    from: from,
                    body: responseMessage,
                    client: twilioClient
                )
                .ConfigureAwait(false);
            // Twilio cannot guarantee order of the messages as it is up to the carrier to deliver the SMS's.
            // by adding a 1s delay between each message, the messages are deliver in the correct order in most cases.
            // alternatively, you could query the status of each message until it is delivered, then send the next.
            await Task.Delay(1000);
        }
    }

    private static List<ChatMessage> GetPreviousMessages(ISession session)
    {
        var jsonBytes = session.Get(PreviousMessagesKey);

        if (jsonBytes == null)
        {
            return new List<ChatMessage>();
        }

        var messages = new List<ChatMessage>();
        var json = JsonSerializer.Deserialize<JsonDocument>(jsonBytes);
        foreach (var messageJsonObject in json.RootElement.EnumerateArray())
        {
            var role = messageJsonObject.GetProperty("Role").GetProperty("Label").GetString();
            var content = messageJsonObject.GetProperty("Content").GetString();
            var chatMessage = new ChatMessage(new ChatRole(role), content);
            messages.Add(chatMessage);
        }

        return messages;
    }

    private static void SetPreviousMessages(ISession session, List<ChatMessage> messages)
    {
        var serializedJson = JsonSerializer.SerializeToUtf8Bytes(messages);
        session.Set(PreviousMessagesKey, serializedJson);
    }

    private static void RemovePreviousMessages(ISession session)
        => session.Remove(PreviousMessagesKey);
}