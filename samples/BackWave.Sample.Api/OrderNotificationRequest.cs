using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BackWave.Sample.Api;

/// <summary>
/// The JSON request body for <c>POST /jobs/order-notification</c>: a small flat "document"
/// (an order-notification shape) that maps to the generated <c>OrderNotification</c> payload.
/// Kept as its own request record so the Swagger body stays clean and the <c>fail</c> toggle is
/// driven by the endpoint's query string rather than the body.
/// </summary>
public sealed record OrderNotificationRequest(
    string OrderRef,
    string CustomerEmail,
    string Channel,
    int ItemCount,
    decimal TotalAmount);

/// <summary>
/// Fills in a realistic, pre-filled Swagger example for <see cref="OrderNotificationRequest"/> so
/// the endpoint is one click to send. Uses the Swashbuckle <c>ISchemaFilter</c> already available
/// in this setup (no new package): the example renders verbatim in Swagger's "Try it out" body.
/// </summary>
public sealed class OrderNotificationRequestExample : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(OrderNotificationRequest) || schema is not OpenApiSchema concrete)
        {
            return;
        }

        concrete.Example = new JsonObject
        {
            ["orderRef"] = "ORD-10427",
            ["customerEmail"] = "ada@example.com",
            ["channel"] = "email",
            ["itemCount"] = 3,
            ["totalAmount"] = 129.95m,
        };
    }
}
