using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebDS_Test.Services.Shipping;

/// <summary>
/// LibanPost carrier client.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is coded against an assumed contract, not a verified one.</b> The methods, request body
/// and response mapping below are shaped to the way postal carrier APIs normally work — a booking
/// call that returns a consignment id, tracking number and label, and a tracking call that returns
/// a status plus an event list. When the real LibanPost API documentation is available, the three
/// places to reconcile are marked <c>CONTRACT:</c> in the code: the endpoint paths, the request
/// body field names, and the status-code mapping in <see cref="MapStatus"/>.
/// </para>
/// <para>
/// Until then this class is registered but not selected: <c>Shipping:Provider</c> defaults to
/// <c>simulator</c>, so the fulfilment path works end to end without pretending a real carrier
/// accepted anything. Selecting this provider without credentials throws at startup rather than
/// failing silently at booking time.
/// </para>
/// </remarks>
public sealed class LibanPostCarrier : IShippingCarrier
{
    private readonly HttpClient _http;
    private readonly ShippingCarrierOptions _options;
    private readonly ILogger<LibanPostCarrier> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LibanPostCarrier(HttpClient http, IOptions<ShippingCarrierOptions> options, ILogger<LibanPostCarrier> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }

        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            var header = string.IsNullOrWhiteSpace(_options.ApiKeyHeader) ? "Authorization" : _options.ApiKeyHeader!;

            if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }
            else
            {
                _http.DefaultRequestHeaders.Remove(header);
                _http.DefaultRequestHeaders.Add(header, _options.ApiKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.AccountNumber))
        {
            _http.DefaultRequestHeaders.Remove("X-Account-Number");
            _http.DefaultRequestHeaders.Add("X-Account-Number", _options.AccountNumber);
        }

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Key => "libanpost";

    public string DisplayName => "LibanPost";

    public bool IsLive => true;

    public async Task<ShipmentReceipt> CreateShipmentAsync(ShipmentRequest request, CancellationToken ct = default)
    {
        if (request.CodAmount > _options.MaxCodAmount)
        {
            return ShipmentReceipt.Failed(
                $"Cash on delivery of {request.CodAmount:N2} exceeds the marketplace ceiling of {_options.MaxCodAmount:N2}.");
        }

        // CONTRACT: request body field names.
        var body = new
        {
            accountNumber = _options.AccountNumber,
            reference = request.OrderNumber,
            serviceType = "DOMESTIC",
            parcels = request.ParcelCount,
            weightGrams = request.WeightGrams,
            currency = request.Currency,
            cashOnDelivery = _options.EnableCashOnDelivery ? request.CodAmount : 0m,
            pickup = new
            {
                location = request.PickupLocation,
                address = request.PickupAddress,
                phone = request.PickupPhone,
                contact = request.ShopName
            },
            destination = new
            {
                name = request.Delivery.RecipientName,
                phone = request.Delivery.Phone,
                address1 = request.Delivery.Line1,
                address2 = request.Delivery.Line2,
                city = request.Delivery.City,
                state = request.Delivery.State,
                postalCode = request.Delivery.PostalCode,
                country = request.Delivery.Country
            },
            notes = request.Notes
        };

        try
        {
            // CONTRACT: endpoint path.
            using var response = await _http.PostAsJsonAsync("api/shipments", body, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("LibanPost booking failed for {OrderNumber}: {Status} {Detail}",
                    request.OrderNumber, (int)response.StatusCode, Truncate(detail));

                return ShipmentReceipt.Failed($"Carrier rejected the consignment ({(int)response.StatusCode}).");
            }

            var payload = await response.Content.ReadFromJsonAsync<CreateShipmentResponse>(JsonOptions, ct);

            if (payload is null || string.IsNullOrWhiteSpace(payload.TrackingNumber))
            {
                return ShipmentReceipt.Failed("Carrier response did not include a tracking number.");
            }

            _logger.LogInformation("LibanPost consignment {Tracking} booked for order {OrderNumber}",
                payload.TrackingNumber, request.OrderNumber);

            return new ShipmentReceipt(
                true,
                payload.ShipmentId,
                payload.TrackingNumber,
                // Some carriers return a label URL, others return the label bytes. Both are supported.
                payload.LabelUrl ?? BuildLabelDataUri(payload.LabelBase64, payload.LabelContentType),
                payload.Cost ?? _options.DefaultShippingCost);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "LibanPost booking failed for {OrderNumber}", request.OrderNumber);
            return ShipmentReceipt.Failed("Could not reach the carrier. The consignment was not booked.");
        }
    }

    public async Task<TrackingResult> TrackAsync(string trackingNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            return new TrackingResult(false, ShipmentStatus.Draft, Array.Empty<TrackingEvent>(), "No tracking number supplied.");
        }

        try
        {
            // CONTRACT: endpoint path and query parameter name.
            using var response = await _http.GetAsync($"api/tracking/{Uri.EscapeDataString(trackingNumber)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                return new TrackingResult(false, ShipmentStatus.Draft, Array.Empty<TrackingEvent>(),
                    $"Carrier returned {(int)response.StatusCode} for that tracking number.");
            }

            var payload = await response.Content.ReadFromJsonAsync<TrackingResponse>(JsonOptions, ct);

            if (payload is null)
            {
                return new TrackingResult(false, ShipmentStatus.Draft, Array.Empty<TrackingEvent>(),
                    "Carrier returned an empty tracking response.");
            }

            var events = (payload.Events ?? new List<TrackingEventDto>())
                .Select(e => new TrackingEvent(
                    MapStatus(e.StatusCode ?? e.Status),
                    e.Location,
                    e.Description,
                    e.OccurredUtc ?? e.Timestamp ?? DateTime.UtcNow))
                .OrderBy(e => e.OccurredUtc)
                .ToList();

            if (events.Count == 0 && !string.IsNullOrWhiteSpace(payload.Status))
            {
                events.Add(new TrackingEvent(MapStatus(payload.StatusCode ?? payload.Status), payload.Location,
                    payload.Description, payload.UpdatedUtc ?? DateTime.UtcNow));
            }

            var status = events.Count > 0 ? events[^1].Status : MapStatus(payload.StatusCode ?? payload.Status);

            return new TrackingResult(true, status, events);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "LibanPost tracking failed for {TrackingNumber}", trackingNumber);
            return new TrackingResult(false, ShipmentStatus.Draft, Array.Empty<TrackingEvent>(),
                "Could not reach the carrier to refresh tracking.");
        }
    }

    public async Task<ShippingQuote> QuoteAsync(ShipmentRequest request, CancellationToken ct = default)
    {
        // CONTRACT: endpoint path and body.
        var body = new
        {
            accountNumber = _options.AccountNumber,
            weightGrams = request.WeightGrams,
            parcels = request.ParcelCount,
            destinationCity = request.Delivery.City,
            destinationCountry = request.Delivery.Country,
            cashOnDelivery = request.CodAmount
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("api/rates", body, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                // A quote failure must not block fulfilment; fall back to the configured default.
                return new ShippingQuote(true, _options.DefaultShippingCost, _options.DefaultTransitDays,
                    "Default rate (carrier quote unavailable)");
            }

            var payload = await response.Content.ReadFromJsonAsync<QuoteResponse>(JsonOptions, ct);

            return payload is null
                ? new ShippingQuote(true, _options.DefaultShippingCost, _options.DefaultTransitDays, "Default rate")
                : new ShippingQuote(true, payload.Cost, payload.TransitDays, payload.ServiceName);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "LibanPost quote failed; using the default rate");
            return new ShippingQuote(true, _options.DefaultShippingCost, _options.DefaultTransitDays, "Default rate");
        }
    }

    public async Task<ShipmentReceipt> CancelAsync(string trackingNumber, CancellationToken ct = default)
    {
        try
        {
            // CONTRACT: endpoint path.
            using var response = await _http.DeleteAsync($"api/shipments/{Uri.EscapeDataString(trackingNumber)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                return ShipmentReceipt.Failed($"Carrier refused the cancellation ({(int)response.StatusCode}).");
            }

            _logger.LogInformation("LibanPost consignment {TrackingNumber} cancelled", trackingNumber);
            return new ShipmentReceipt(true, null, trackingNumber, null, 0m);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "LibanPost cancellation failed for {TrackingNumber}", trackingNumber);
            return ShipmentReceipt.Failed("Could not reach the carrier to cancel the consignment.");
        }
    }

    /// <summary>
    /// CONTRACT: maps a carrier status code onto our shipment state. Carrier vocabularies differ,
    /// so unknown codes fall back to <see cref="ShipmentStatus.InTransit"/> rather than throwing —
    /// an unrecognised code should not break a vendor's tracking page.
    /// </summary>
    private static ShipmentStatus MapStatus(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "BOOKED" or "CREATED" or "LABEL_CREATED" or "REGISTERED" => ShipmentStatus.Created,
            "PICKED_UP" or "COLLECTED" or "PICKUP" => ShipmentStatus.PickedUp,
            "IN_TRANSIT" or "TRANSIT" or "ARRIVED" or "DEPARTED" or "CUSTOMS" => ShipmentStatus.InTransit,
            "OUT_FOR_DELIVERY" or "WITH_COURIER" or "ON_DELIVERY" => ShipmentStatus.OutForDelivery,
            "DELIVERED" or "COMPLETED" => ShipmentStatus.Delivered,
            "FAILED" or "DELIVERY_FAILED" or "REFUSED" or "UNDELIVERED" => ShipmentStatus.Failed,
            "RETURNED" or "RETURN_TO_SENDER" or "RTS" => ShipmentStatus.Returned,
            "CANCELLED" or "CANCELED" => ShipmentStatus.Cancelled,
            _ => ShipmentStatus.InTransit
        };

    private static string? BuildLabelDataUri(string? base64, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        // Reject anything that is not a PDF or image, so a carrier response cannot be used to
        // inject an arbitrary data URI into the page.
        var type = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType;
        if (!type.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) &&
            !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"data:{type};base64,{base64}";
    }

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400] + "…";

    // ---- Wire shapes (assumed; see the CONTRACT notes above) ----------

    private sealed class CreateShipmentResponse
    {
        [JsonPropertyName("shipmentId")] public string? ShipmentId { get; set; }
        [JsonPropertyName("consignmentId")] public string? ConsignmentId { get; set; }
        [JsonPropertyName("trackingNumber")] public string? TrackingNumber { get; set; }
        [JsonPropertyName("labelUrl")] public string? LabelUrl { get; set; }
        [JsonPropertyName("labelBase64")] public string? LabelBase64 { get; set; }
        [JsonPropertyName("labelContentType")] public string? LabelContentType { get; set; }
        [JsonPropertyName("cost")] public decimal? Cost { get; set; }
    }

    private sealed class TrackingResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("statusCode")] public string? StatusCode { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("updatedUtc")] public DateTime? UpdatedUtc { get; set; }
        [JsonPropertyName("events")] public List<TrackingEventDto>? Events { get; set; }
    }

    private sealed class TrackingEventDto
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("statusCode")] public string? StatusCode { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("occurredUtc")] public DateTime? OccurredUtc { get; set; }
        [JsonPropertyName("timestamp")] public DateTime? Timestamp { get; set; }
    }

    private sealed class QuoteResponse
    {
        [JsonPropertyName("cost")] public decimal Cost { get; set; }
        [JsonPropertyName("transitDays")] public int TransitDays { get; set; }
        [JsonPropertyName("serviceName")] public string? ServiceName { get; set; }
    }
}
