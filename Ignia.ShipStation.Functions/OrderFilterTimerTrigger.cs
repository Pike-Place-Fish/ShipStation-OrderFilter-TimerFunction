/*==============================================================================================================================
| Author        Ignia, LLC
| Client        Ignia, LLC
| Project       ShipStation Order Filter
\=============================================================================================================================*/
using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Ignia.ShipStation.Functions {

  /*============================================================================================================================
  | CLASS: ORDER FILTER TIMER TRIGGER
  \---------------------------------------------------------------------------------------------------------------------------*/
  /// <summary>
  ///   Class wrapper for Run method required for Azure Timer Trigger functions, as well as supporting methods used for sending
  ///   JSON payloads through to the ShipStation Order Event Handler.
  /// </summary>
  public static class OrderFilterTimerTrigger {

    /*==========================================================================================================================
    | PRIVATE MEMBERS
    \-------------------------------------------------------------------------------------------------------------------------*/
    private static string       _shipStationBaseUrl             = "https://ssapi.shipstation.com";
    private static string       _shipStationApiKey              = Environment.GetEnvironmentVariable("ShipStationApiKey");
    private static string       _shipStationClientSecret        = Environment.GetEnvironmentVariable("ShipStationClientSecret");
    private static string       _orderEventHandlerBaseUrl       = "https://igniashopify.azurewebsites.net/ShipStation/OrderFilter/Shared/Handlers/OrderEventHandler.ashx";

    /*==========================================================================================================================
    | SHIPSTATION HTTP CLIENT
    \-------------------------------------------------------------------------------------------------------------------------*/
    /// <summary>
    ///   Instantiates the ShipStation HTTP client.
    /// </summary>
    public static HttpClient ShipStationHttpClient {
      get {

        /*----------------------------------------------------------------------------------------------------------------------
        | Configure HTTP client authorization
        \---------------------------------------------------------------------------------------------------------------------*/
        string clientCredentials = Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes(_shipStationApiKey + ":" + _shipStationClientSecret));

        /*----------------------------------------------------------------------------------------------------------------------
        | Configure HTTP client
        \---------------------------------------------------------------------------------------------------------------------*/
        var httpClient          = new HttpClient();
        httpClient.Timeout      = new TimeSpan(0, 5, 0);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
          "authorization",
          "Basic " + clientCredentials
        );

        /*----------------------------------------------------------------------------------------------------------------------
        | Return client
        \---------------------------------------------------------------------------------------------------------------------*/
        return httpClient;

      }
    }

    /*==========================================================================================================================
    | HANDLER HTTP CLIENT
    \-------------------------------------------------------------------------------------------------------------------------*/
    /// <summary>
    ///   Instantiates the Order Event Handler HTTP client.
    /// </summary>
    public static HttpClient HandlerHttpClient {
      get {

        /*----------------------------------------------------------------------------------------------------------------------
        | Configure HTTP client
        \---------------------------------------------------------------------------------------------------------------------*/
        var httpClient          = new HttpClient();
        httpClient.Timeout      = new TimeSpan(0, 5, 0);

        /*----------------------------------------------------------------------------------------------------------------------
        | Return client
        \---------------------------------------------------------------------------------------------------------------------*/
        return httpClient;

      }
    }

    /*==========================================================================================================================
    | METHOD: RUN
    \-------------------------------------------------------------------------------------------------------------------------*/
    /// <summary>
    ///   Required method for the Azure Timed Trigger function. Responds to bindings defined in the associated
    ///   <see href="https://docs.microsoft.com/en-us/azure/azure-functions/functions-reference#function-code">function.json</see>
    ///   configuration.
    /// </summary>
    /// <param name="timerInfo">The timer trigger object defined in the function.json bindings.</param>
    /// <param name="log">The current logging context.</param>
    [FunctionName("OrderFilterTimerTrigger")]
    public static async Task Run([TimerTrigger("0 */15 * * * *")]TimerInfo timerInfo, TraceWriter log) {

      /*------------------------------------------------------------------------------------------------------------------------
      | Log initial firing of the trigger function
      \-----------------------------------------------------------------------------------------------------------------------*/
      log.Info($"OrderFilterTimerTrigger function executed at: {DateTime.Now}");

      /*------------------------------------------------------------------------------------------------------------------------
      | Establish variables for ShipStation API call
      \-----------------------------------------------------------------------------------------------------------------------*/
      string ordersEndpoint     = _shipStationBaseUrl + "/orders?orderStatus=awaiting_shipment&sortDir=DESC&pageSize=1";
      double totalOrders        = 0.00;
      int pagesToProcess        = 0;
      ExceptionDispatchInfo capturedException   = null;

      /*------------------------------------------------------------------------------------------------------------------------
      | Retrieve the metadata from the ShipStation API's orders endpoint to determine the number of calls to make
      \-----------------------------------------------------------------------------------------------------------------------*/
      Task<string> apiResult    = GetApiResourceAsync(ordersEndpoint);
      string ordersMetadata     = await apiResult;

      try {
        dynamic payload         = JObject.Parse(ordersMetadata);

        if (payload != null && payload.total != null) {
          totalOrders           = Convert.ToDouble(payload.total);
          pagesToProcess        = Int32.Parse(Math.Ceiling(totalOrders/100.00).ToString());
        }

        log.Info("URLs to send: " + pagesToProcess);
      }
      catch(FormatException ex) {
        capturedException       = ExceptionDispatchInfo.Capture(ex);
      }
      if (capturedException != null) {
        log.Error("ShipStation orders metadata parse error: " + capturedException.SourceException.Message);
        capturedException       = null;
        return;
      }

      /*------------------------------------------------------------------------------------------------------------------------
      | Send ShipStation API Orders endpoint URLs through to the Order Event Handler, for further processing
      \-----------------------------------------------------------------------------------------------------------------------*/
      try {

        var callQueue           = new List<Task<HttpResponseMessage>>();

        // Iterate through all available pages of data to form the appropriate endpoint URLs
        for (int i = pagesToProcess; i > 0; i--) {

          // Reset ShipStation Orders endpoint based on current page
          ordersEndpoint        = _shipStationBaseUrl + "/orders?orderStatus=awaiting_shipment&sortDir=DESC&page=" + i.ToString();

          // Build the string response content expected by the Order Event Handler
          string postData       = "{\"resource_url\":\"" + ordersEndpoint + "\",\"resource_type\":\"OrderFilterTimerTrigger\"}";
          var content           = new StringContent(postData, Encoding.UTF8, "application/json");

          // Add the POST to the call queue
          callQueue.Add(HandlerHttpClient.PostAsync(_orderEventHandlerBaseUrl, content));

        }

        // Process the handler call queue
        log.Info("callQueue.Count: " + callQueue.Count.ToString());
        while (callQueue.Count > 0) {
          Task<HttpResponseMessage> httpResponseMessage = await Task.WhenAny(callQueue);
          callQueue.Remove(httpResponseMessage);
        }

      }
      catch(FormatException formatException) {
        capturedException       = ExceptionDispatchInfo.Capture(formatException);
      }
      catch (Exception exception) {
        capturedException       = ExceptionDispatchInfo.Capture(exception);
      }
      finally {
      }
      if (capturedException != null) {
        log.Error("Error processing Order Event Handler POSTs: " + capturedException.SourceException.Message);
        capturedException       = null;
        return;
      }

    }

    /*==========================================================================================================================
    | GET API RESOURCE
    \-------------------------------------------------------------------------------------------------------------------------*/
    /// <summary>
    ///   Retrieves the JSON response from the requested resource URL.
    /// </summary>
    /// <param name="resourceUrl">The resource (API endpoint) to call.</param>
    /// <returns>A JSON response from the ShipStation API, based on the GET request to the resource URL.</returns>
    private static async Task<string> GetApiResourceAsync(string resourceUrl) {
      string apiResult          = "";
      using (ShipStationHttpClient) {
        using (var response = await ShipStationHttpClient.GetAsync(resourceUrl)) {
          apiResult             = await response.Content.ReadAsStringAsync();
        }
      }
      return apiResult;
    }

  } // Class

} // Namespace
