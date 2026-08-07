using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace LearningAzure.Lessons.QueueStorage;

/// <summary>
/// Makes at-least-once delivery visible: the same message, delivered three
/// times, because a handler was slower than its visibility timeout.
/// </summary>
/// <remarks>
/// Everything printed here is a real response from Azurite. Nothing is
/// simulated: the redelivery in section 3 happens because the program genuinely
/// waits longer than the visibility window it asked for.
/// </remarks>
internal static class Program
{
    /// <summary>The emulator alias. It carries no key: the SDK expands it locally.</summary>
    private const string EmulatorConnectionString = "UseDevelopmentStorage=true";

    private const string QueueName = "expedition-dispatch";

    private static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING") ?? EmulatorConnectionString;

        var queue = new QueueClient(connectionString, QueueName);

        try
        {
            await queue.CreateIfNotExistsAsync().ConfigureAwait(false);
            await DrainAsync(queue).ConfigureAwait(false);

            await ShowMessageShapeAsync(queue).ConfigureAwait(false);
            await ShowReceiveAndDeleteAsync(queue).ConfigureAwait(false);
            await ShowRedeliveryAsync(queue).ConfigureAwait(false);
            await ShowPeekAndDepthAsync(queue).ConfigureAwait(false);
            ShowSizeCeiling();
        }
        catch (RequestFailedException error)
        {
            Console.Error.WriteLine($"The service rejected a request: {error.ErrorCode} (HTTP {error.Status}).");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (Exception error) when (error is HttpRequestException or AggregateException)
        {
            Console.Error.WriteLine(
                "Could not reach Azurite on 127.0.0.1:10001. Start it with "
                + "'docker compose up -d azurite' and try again.");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            await queue.DeleteIfExistsAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Section 1: what the service actually stores and hands back.</summary>
    private static async Task ShowMessageShapeAsync(QueueClient queue)
    {
        Console.WriteLine("1. What a queue message is");
        Console.WriteLine("--------------------------");

        var body = Convert.ToBase64String("""{"workOrderId":"wo-1001","operation":"ingest"}"""u8.ToArray());
        var receipt = await queue.SendMessageAsync(body).ConfigureAwait(false);

        Console.WriteLine($"   Sent message id      : {receipt.Value.MessageId}");
        Console.WriteLine($"   Pop receipt          : {receipt.Value.PopReceipt}");
        Console.WriteLine($"   Inserted (UTC)       : {receipt.Value.InsertionTime:u}");
        Console.WriteLine($"   Expires  (UTC)       : {receipt.Value.ExpirationTime:u}");
        Console.WriteLine($"   Default lifetime     : {receipt.Value.ExpirationTime - receipt.Value.InsertionTime}");
        Console.WriteLine(
            "   The message id identifies the QUEUE ENTRY, not the work. Re-sending the");
        Console.WriteLine(
            "   same payload produces a different id, which is why deduplication keys off");
        Console.WriteLine("   the producer-chosen work order id instead.");
        Console.WriteLine();
    }

    /// <summary>Section 2: receive hides, delete removes. They are two calls.</summary>
    private static async Task ShowReceiveAndDeleteAsync(QueueClient queue)
    {
        Console.WriteLine("2. Receive hides; delete removes");
        Console.WriteLine("--------------------------------");

        var received = await queue
            .ReceiveMessagesAsync(maxMessages: 1, visibilityTimeout: TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        var message = received.Value[0];

        Console.WriteLine($"   Received id          : {message.MessageId}");
        Console.WriteLine($"   Dequeue count        : {message.DequeueCount}");
        Console.WriteLine($"   Invisible until (UTC): {message.NextVisibleOn:u}");

        var depthWhileHidden = await queue.GetPropertiesAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"   ApproximateMessagesCount while it is hidden: {depthWhileHidden.Value.ApproximateMessagesCount}");
        Console.WriteLine(
            "   The message still counts toward the depth. It is invisible, not gone.");

        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt).ConfigureAwait(false);

        var depthAfterDelete = await queue.GetPropertiesAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"   ApproximateMessagesCount after delete     : {depthAfterDelete.Value.ApproximateMessagesCount}");
        Console.WriteLine();
    }

    /// <summary>Section 3: the same message, three times, because the handler was slow.</summary>
    private static async Task ShowRedeliveryAsync(QueueClient queue)
    {
        Console.WriteLine("3. At-least-once, observed");
        Console.WriteLine("--------------------------");

        var visibility = TimeSpan.FromSeconds(1);
        var handlerDuration = TimeSpan.FromMilliseconds(1500);

        await queue
            .SendMessageAsync(Convert.ToBase64String("""{"workOrderId":"wo-2002"}"""u8.ToArray()))
            .ConfigureAwait(false);

        Console.WriteLine(
            $"   Visibility timeout   : {visibility.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
        Console.WriteLine(
            "   Handler duration     : "
            + $"{handlerDuration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s (deliberately longer)");
        Console.WriteLine();

        QueueMessage? last = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var received = await queue
                .ReceiveMessagesAsync(maxMessages: 1, visibilityTimeout: visibility)
                .ConfigureAwait(false);

            if (received.Value.Length == 0)
            {
                Console.WriteLine($"   Attempt {attempt}: nothing visible yet.");
                await Task.Delay(visibility).ConfigureAwait(false);
                continue;
            }

            last = received.Value[0];
            Console.WriteLine(
                $"   Attempt {attempt}: id {last.MessageId[..8]}... DequeueCount={last.DequeueCount} "
                + $"(same message: {(attempt == 1 ? "first delivery" : "REDELIVERED")})");

            // The "handler" runs longer than the visibility window it was given.
            await Task.Delay(handlerDuration).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine(
            "   The DequeueCount is the service telling you how many consumers have");
        Console.WriteLine(
            "   already been handed this work. Nothing here was retried on error: every");
        Console.WriteLine(
            "   redelivery is purely the visibility timeout expiring while work was in");
        Console.WriteLine("   flight.");

        if (last is not null)
        {
            try
            {
                await queue.DeleteMessageAsync(last.MessageId, last.PopReceipt).ConfigureAwait(false);
                Console.WriteLine("   Deleting with the newest pop receipt succeeded.");
            }
            catch (RequestFailedException error)
            {
                Console.WriteLine($"   Delete rejected: {error.ErrorCode} (HTTP {error.Status}).");
            }
        }

        Console.WriteLine();
    }

    /// <summary>Section 4: peek reads without claiming; depth is approximate.</summary>
    private static async Task ShowPeekAndDepthAsync(QueueClient queue)
    {
        Console.WriteLine("4. Peeking does not claim");
        Console.WriteLine("-------------------------");

        for (var n = 1; n <= 3; n++)
        {
            var body = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{{\"workOrderId\":\"wo-30{n:00}\"}}"));
            await queue.SendMessageAsync(body).ConfigureAwait(false);
        }

        var peeked = await queue.PeekMessagesAsync(maxMessages: 3).ConfigureAwait(false);

        foreach (var message in peeked.Value)
        {
            Console.WriteLine(
                $"   Peeked {message.MessageId[..8]}... DequeueCount={message.DequeueCount} "
                + $"body={Encoding.UTF8.GetString(Convert.FromBase64String(message.Body.ToString()))}");
        }

        Console.WriteLine(
            "   Peek returns no pop receipt, so a peeked message cannot be deleted, and");
        Console.WriteLine(
            "   its DequeueCount does not advance. Peek is for dashboards, not consumers.");

        var properties = await queue.GetPropertiesAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"   ApproximateMessagesCount: {properties.Value.ApproximateMessagesCount} "
            + "(named 'Approximate' because it is a snapshot, not a lock)");
        Console.WriteLine();
    }

    /// <summary>Section 5: the encoded ceiling, computed rather than recited.</summary>
    private static void ShowSizeCeiling()
    {
        Console.WriteLine("5. The 64 KiB ceiling is a Base64 ceiling");
        Console.WriteLine("-----------------------------------------");

        const int limit = 64 * 1024;

        foreach (var payload in (int[])[48 * 1024, 49 * 1024, 60 * 1024])
        {
            var encoded = Convert.ToBase64String(new byte[payload]).Length;
            Console.WriteLine(
                $"   {payload,6} raw bytes -> {encoded,6} encoded bytes -> "
                + $"{(encoded <= limit ? "fits" : "REJECTED")}");
        }

        Console.WriteLine(
            "   The usable payload is about 48 KiB, not 64. Anything bigger belongs in a");
        Console.WriteLine("   blob, with the queue carrying only its name.");
        Console.WriteLine();
    }

    /// <summary>Removes anything a previous run left behind, so output is repeatable.</summary>
    private static async Task DrainAsync(QueueClient queue)
    {
        await queue.ClearMessagesAsync().ConfigureAwait(false);
    }
}
