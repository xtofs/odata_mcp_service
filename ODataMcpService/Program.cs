using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using System.Text.Json;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using System.Net.Http.Headers;

namespace ODataMcpService;

public class ToolConfiguration
{
    public bool EnableCount { get; set; } = true;  // Default: +C
    public bool EnableGet { get; set; } = true;    // Default: +g  
    public bool EnableFilter { get; set; } = false; // Default: -f
}

class Program
{
    private static readonly HttpClient _httpClient = new();
    private static IEdmModel? _edmModel;
    private static string? _odataBaseUrl;
    private static ToolConfiguration _toolConfig = new();

    static async Task Main(string[] args)
    {
        ConfigureLogging();

        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: ODataMcpService <odata-metadata-url> [tool-options]");
                Console.WriteLine("Tool Options:");
                Console.WriteLine("  +c / -c   Enable/disable count tools (default: +c)");
                Console.WriteLine("  +g / -g   Enable/disable get tools (default: +g)");
                Console.WriteLine("  +f / -f   Enable/disable filter tools (default: -f)");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  ODataMcpService https://services.odata.org/V4/Northwind/Northwind.svc");
                Console.WriteLine("  ODataMcpService https://services.odata.org/V4/Northwind/Northwind.svc +f");
                Console.WriteLine("  ODataMcpService https://services.odata.org/V4/Northwind/Northwind.svc -c +f");
                return;
            }

            var (metadataUrl, toolConfig) = ParseArguments(args);
            _toolConfig = toolConfig;

            // Extract base URL from metadata URL
            _odataBaseUrl = metadataUrl.Replace("/$metadata", "");

            // Load and parse the OData metadata
            await LoadODataMetadata(metadataUrl);

            Log.Information("Starting MCP bridge to {MetadataUrl}", metadataUrl);

            // Create tools from entity sets
            var tools = await CreateEntitySetTools(_toolConfig);

            // Configure MCP server options
            var options = new McpServerOptions()
            {
                ServerInfo = new Implementation { Name = "odata-mcp-server", Version = "1.0.0" },
                Handlers = new McpServerHandlers()
                {
                    ListToolsHandler = (request, cancellationToken) =>
                        ValueTask.FromResult(new ListToolsResult { Tools = tools }),

                    CallToolHandler = async (request, cancellationToken) =>
                    {
                        var toolName = request.Params?.Name;
                        if (string.IsNullOrEmpty(toolName))
                        {
                            throw new McpProtocolException("Tool name is required", McpErrorCode.InvalidParams);
                        }

                        if (toolName.StartsWith("count_"))
                        {
                            if (!_toolConfig.EnableCount)
                            {
                                throw new McpProtocolException("Count tools are not enabled. Use +C to enable count capabilities.", McpErrorCode.InvalidRequest);
                            }
                            
                            var entitySetName = ExtractEntitySetName(toolName, "count_");
                            return await CountEntities(entitySetName);
                        }

                        if (toolName.StartsWith("get_"))
                        {
                            if (!_toolConfig.EnableGet)
                            {
                                throw new McpProtocolException("Get tools are not enabled. Use +g to enable get capabilities.", McpErrorCode.InvalidRequest);
                            }
                            
                            var entitySetName = ExtractEntitySetName(toolName, "get_");
                            var top = ExtractTopParameter(request.Params?.Arguments);
                            return await GetEntities(entitySetName, top);
                        }

                        if (toolName.StartsWith("filter_"))
                        {
                            if (!_toolConfig.EnableFilter)
                            {
                                throw new McpProtocolException("Filter tools are not enabled. Use +f to enable filtering capabilities.", McpErrorCode.InvalidRequest);
                            }
                            
                            var entitySetName = ExtractEntitySetName(toolName, "filter_");
                            return await FilterEntities(entitySetName, request.Params?.Arguments);
                        }

                        throw new McpProtocolException($"Unknown tool: '{toolName}'", McpErrorCode.InvalidRequest);
                    }
                }
            };

            // Create and start the server
            await using var server = McpServer.Create(new StdioServerTransport("odata-mcp-server"), options);
            await server.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            Environment.Exit(1);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static bool ConfigureLogging()
    {
        // Detect if running in TTY (interactive terminal) vs non-TTY (MCP stdio mode)
        bool isInteractive = Console.IsInputRedirected == false && Console.IsOutputRedirected == false;

        // Configure Serilog based on TTY detection
        var loggerConfig = new LoggerConfiguration();

        if (isInteractive)
        {
            // TTY mode - log to console for interactive debugging
            loggerConfig = loggerConfig
                .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
        else
        {
            // Non-TTY mode (MCP stdio) - log to file only to avoid interfering with MCP protocol
            loggerConfig = loggerConfig
                .WriteTo.File(
                    path: @"logs\odata-mcp-.log",
                    rollingInterval: RollingInterval.Hour,
                    retainedFileCountLimit: 168, // Keep 7 days worth of hourly logs (24 * 7)
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = loggerConfig.CreateLogger();
        return isInteractive;
    }

    private static (string metadataUrl, ToolConfiguration toolConfig) ParseArguments(string[] args)
    {
        var metadataUrl = args[0];
        var toolConfig = new ToolConfiguration(); // Start with defaults

        // Process tool configuration options
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLower())
            {
                case "+c":
                    toolConfig.EnableCount = true;
                    break;
                case "-c":
                    toolConfig.EnableCount = false;
                    break;
                case "+g":
                    toolConfig.EnableGet = true;
                    break;
                case "-g":
                    toolConfig.EnableGet = false;
                    break;
                case "+f":
                    toolConfig.EnableFilter = true;
                    break;
                case "-f":
                    toolConfig.EnableFilter = false;
                    break;
                default:
                    Log.Warning("Unknown tool option: {Option}", arg);
                    break;
            }
        }

        if (metadataUrl.EndsWith('/'))
        {
            metadataUrl = metadataUrl.TrimEnd('/');
        }
        if (!metadataUrl.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase))
        {
            metadataUrl = $"{metadataUrl}/$metadata";
        }

        return (metadataUrl, toolConfig);
    }

    private static string GetMetaDataUrlFromArgs(string[] args)
    {
        var metadataUrl = args[0];
        if (metadataUrl.EndsWith('/'))
        {
            metadataUrl = metadataUrl.TrimEnd('/');
        }
        if (!metadataUrl.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase))
        {
            metadataUrl = $"{metadataUrl}/$metadata";
        }

        return metadataUrl;
    }

    private static async Task LoadODataMetadata(string metadataUrl)
    {
        try
        {
            var metadataXml = await LoadMetadataAsXmlReader(metadataUrl);

            if (!CsdlReader.TryParse(metadataXml, out _edmModel, out var errors))
            {
                var errorMessages = string.Join("\n", errors.Select(e => e.ToString()));
                throw new InvalidOperationException($"Failed to parse OData metadata:\n{errorMessages}");
            }

            Log.Information("OData metadata loaded successfully");
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Failed to fetch OData metadata from {MetadataUrl}", metadataUrl);
            throw new InvalidOperationException($"Failed to fetch OData metadata: {ex.Message}", ex);
        }
    }

    private static async Task<XmlReader> LoadMetadataAsXmlReader(string metadataUrl)
    {
        Log.Information("Loading OData metadata from: {MetadataUrl}", metadataUrl);

#if DUMP_METADATA
        {
            var debug_request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
            debug_request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

            var debug_response = await _httpClient.SendAsync(debug_request);
            File.WriteAllText("metadata.xml", await debug_response.Content.ReadAsStringAsync());
        }
#endif
        var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var metadataStream = await response.Content.ReadAsStreamAsync();

        var metadataXml = XmlReader.Create(metadataStream);
        return metadataXml;
    }

    private static async Task<List<Tool>> CreateEntitySetTools(ToolConfiguration config)
    {
        if (_edmModel == null)
        {
            throw new InvalidOperationException("EDM model not loaded");
        }

        var entityContainer = _edmModel.EntityContainer;
        if (entityContainer == null)
        {
            throw new InvalidOperationException("No entity container found in the model");
        }

        var entitySets = entityContainer.EntitySets().ToList();
        Log.Information("Found {EntitySetCount} entity sets", entitySets.Count);
        Log.Information("Tool configuration - Count: {CountEnabled}, Get: {GetEnabled}, Filter: {FilterEnabled}", 
                       config.EnableCount, config.EnableGet, config.EnableFilter);

        var tools = new List<Tool>();

        foreach (var entitySet in entitySets)
        {
            if (config.EnableCount)
            {
                tools.Add(CreateCountTool(entitySet));
            }
            
            if (config.EnableGet)
            {
                tools.Add(CreateGetTool(entitySet));
            }
            
            if (config.EnableFilter)
            {
                tools.Add(CreateFilterTool(entitySet));
            }
        }

        return tools;
    }

    private static Tool CreateCountTool(IEdmEntitySet entitySet)
    {
        var toolName = $"count_{entitySet.Name.ToLower()}";
        var description = $"Count the number of entities in the {entitySet.Name} entity set";

        Log.Information("Registering tool: {ToolName}", toolName);

        return new Tool
        {
            Name = toolName,
            Description = description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)
        };
    }

    private static Tool CreateGetTool(IEdmEntitySet entitySet)
    {
        var toolName = $"get_{entitySet.Name.ToLower()}";
        var description = $"Get entities from the {entitySet.Name} entity set with optional top parameter";

        Log.Information("Registering tool: {ToolName}", toolName);

        return new Tool
        {
            Name = toolName,
            Description = description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                {
                    "type": "object",
                    "properties": {
                        "top": {
                            "type": "integer",
                            "description": "Number of entities to retrieve (default: 10, max: 100)",
                            "minimum": 1,
                            "maximum": 100
                        }
                    },
                    "required": []
                }
                """)
        };
    }

    private static Tool CreateFilterTool(IEdmEntitySet entitySet)
    {
        var toolName = $"filter_{entitySet.Name.ToLower()}";
        var description = $"List and filter {entitySet.Name} entities with OData query options";

        Log.Information("Registering tool: {ToolName}", toolName);

        return new Tool
        {
            Name = toolName,
            Description = description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                {
                    "type": "object",
                    "properties": {
                        "filter": {
                            "type": "string",
                            "description": "OData $filter expression (e.g., 'Country eq 'USA'', 'Price gt 20')"
                        },
                        "select": {
                            "type": "string",
                            "description": "OData $select expression to specify which properties to return (e.g., 'Name,Price')"
                        },
                        "orderby": {
                            "type": "string",
                            "description": "OData $orderby expression (e.g., 'Name asc', 'Price desc')"
                        },
                        "top": {
                            "type": "integer",
                            "description": "Number of entities to retrieve (default: 10, max: 100)",
                            "minimum": 1,
                            "maximum": 100
                        },
                        "skip": {
                            "type": "integer",
                            "description": "Number of entities to skip for paging",
                            "minimum": 0
                        }
                    },
                    "required": []
                }
                """)
        };
    }

    private static string ExtractEntitySetName(string toolName, string prefix)
    {
        if (!toolName.StartsWith(prefix))
        {
            throw new ArgumentException($"Invalid tool name format: {toolName}");
        }

        var entitySetName = toolName.Substring(prefix.Length);

        // Find the actual entity set name with correct casing
        if (_edmModel?.EntityContainer != null)
        {
            var entitySet = _edmModel.EntityContainer.EntitySets()
                .FirstOrDefault(es => es.Name.Equals(entitySetName, StringComparison.OrdinalIgnoreCase));
            if (entitySet != null)
            {
                return entitySet.Name;
            }
        }

        return entitySetName;
    }

    private static int ExtractTopParameter(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        const int defaultTop = 10;
        const int maxTop = 100;

        if (arguments == null || !arguments.TryGetValue("top", out var topValue))
        {
            return defaultTop;
        }

        if (topValue.ValueKind == JsonValueKind.Number)
        {
            var top = topValue.GetInt32();
            return Math.Max(1, Math.Min(top, maxTop));
        }

        if (topValue.ValueKind == JsonValueKind.String && int.TryParse(topValue.GetString(), out var parsedTop))
        {
            return Math.Max(1, Math.Min(parsedTop, maxTop));
        }

        return defaultTop;
    }

    private static int ExtractSkipParameter(JsonElement skipValue)
    {
        if (skipValue.ValueKind == JsonValueKind.Number)
        {
            var skip = skipValue.GetInt32();
            return Math.Max(0, skip);
        }

        if (skipValue.ValueKind == JsonValueKind.String && int.TryParse(skipValue.GetString(), out var parsedSkip))
        {
            return Math.Max(0, parsedSkip);
        }

        return 0;
    }

    private static async Task<CallToolResult> CountEntities(string entitySetName)
    {
        try
        {
            if (string.IsNullOrEmpty(_odataBaseUrl))
            {
                throw new InvalidOperationException("OData base URL not set");
            }

            // Construct the OData query URL with $count
            var countUrl = $"{_odataBaseUrl}/{entitySetName}/$count";

            Log.Information("Querying count from: {CountUrl}", countUrl);

            var response = await _httpClient.GetAsync(countUrl);
            response.EnsureSuccessStatusCode();

            var countText = await response.Content.ReadAsStringAsync();

            if (int.TryParse(countText.Trim(), out var count))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"The {entitySetName} entity set contains {count} entities.",
                        Type = "text"
                    }]
                };
            }
            else
            {
                throw new McpProtocolException($"Could not parse count result: {countText}", McpErrorCode.InternalError);
            }
        }
        catch (Exception ex) when (ex is not McpProtocolException)
        {
            throw new McpProtocolException($"Error counting entities in {entitySetName}: {ex.Message}", McpErrorCode.InternalError);
        }
    }

    private static async Task<CallToolResult> GetEntities(string entitySetName, int top)
    {
        try
        {
            if (string.IsNullOrEmpty(_odataBaseUrl))
            {
                throw new InvalidOperationException("OData base URL not set");
            }

            // Construct the OData query URL with $top
            var getUrl = $"{_odataBaseUrl}/{entitySetName}?$top={top}";

            Log.Information("Querying entities from: {GetUrl}", getUrl);

            var response = await _httpClient.GetAsync(getUrl);
            response.EnsureSuccessStatusCode();

            var jsonText = await response.Content.ReadAsStringAsync();

            // Parse the JSON to extract just the value array and format it nicely
            using var jsonDoc = JsonDocument.Parse(jsonText);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("value", out var valueArray))
            {
                var formattedJson = JsonSerializer.Serialize(valueArray, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Retrieved {valueArray.GetArrayLength()} entities from {entitySetName} (top {top}):\n\n{formattedJson}",
                        Type = "text"
                    }]
                };
            }
            else
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Retrieved data from {entitySetName} (top {top}):\n\n{jsonText}",
                        Type = "text"
                    }]
                };
            }
        }
        catch (Exception ex) when (ex is not McpProtocolException)
        {
            throw new McpProtocolException($"Error getting entities from {entitySetName}: {ex.Message}", McpErrorCode.InternalError);
        }
    }

    private static async Task<CallToolResult> FilterEntities(string entitySetName, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        try
        {
            if (string.IsNullOrEmpty(_odataBaseUrl))
            {
                throw new InvalidOperationException("OData base URL not set");
            }

            // Build OData query parameters
            var queryParams = new List<string>();

            if (arguments != null)
            {
                if (arguments.TryGetValue("filter", out var filterValue) && filterValue.ValueKind == JsonValueKind.String)
                {
                    var filter = filterValue.GetString();
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
                    }
                }

                if (arguments.TryGetValue("select", out var selectValue) && selectValue.ValueKind == JsonValueKind.String)
                {
                    var select = selectValue.GetString();
                    if (!string.IsNullOrWhiteSpace(select))
                    {
                        queryParams.Add($"$select={Uri.EscapeDataString(select)}");
                    }
                }

                if (arguments.TryGetValue("orderby", out var orderbyValue) && orderbyValue.ValueKind == JsonValueKind.String)
                {
                    var orderby = orderbyValue.GetString();
                    if (!string.IsNullOrWhiteSpace(orderby))
                    {
                        queryParams.Add($"$orderby={Uri.EscapeDataString(orderby)}");
                    }
                }

                if (arguments.TryGetValue("top", out var topValue))
                {
                    var top = ExtractTopParameter(arguments);
                    queryParams.Add($"$top={top}");
                }

                if (arguments.TryGetValue("skip", out var skipValue))
                {
                    var skip = ExtractSkipParameter(skipValue);
                    if (skip > 0)
                    {
                        queryParams.Add($"$skip={skip}");
                    }
                }
            }

            // If no parameters provided, add default top
            if (queryParams.Count == 0 || !queryParams.Any(p => p.StartsWith("$top=")))
            {
                queryParams.Add("$top=10");
            }

            // Construct the OData query URL
            var queryString = string.Join("&", queryParams);
            var filterUrl = $"{_odataBaseUrl}/{entitySetName}?{queryString}";

            Log.Information("Filtering entities from: {FilterUrl}", filterUrl);

            var response = await _httpClient.GetAsync(filterUrl);
            response.EnsureSuccessStatusCode();

            var jsonText = await response.Content.ReadAsStringAsync();

            // Parse the JSON to extract just the value array and format it nicely
            using var jsonDoc = JsonDocument.Parse(jsonText);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("value", out var valueArray))
            {
                var formattedJson = JsonSerializer.Serialize(valueArray, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var queryDescription = queryParams.Count > 0 ? $" with query: {queryString}" : "";

                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Filtered {valueArray.GetArrayLength()} entities from {entitySetName}{queryDescription}:\n\n{formattedJson}",
                        Type = "text"
                    }]
                };
            }
            else
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Filtered data from {entitySetName}:\n\n{jsonText}",
                        Type = "text"
                    }]
                };
            }
        }
        catch (Exception ex) when (ex is not McpProtocolException)
        {
            throw new McpProtocolException($"Error filtering entities from {entitySetName}: {ex.Message}", McpErrorCode.InternalError);
        }
    }
}
