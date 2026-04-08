using DotNetPlugin.NativeBindings.SDK;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web.Script.Serialization;

namespace DotNetPlugin
{
    class SimpleMcpServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Dictionary<string, MethodInfo> _commands = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Type _targetType;
        private readonly McpServerConfig _config;

        public bool IsActivelyDebugging
        {
            get { return Bridge.DbgIsDebugging(); }
            set { /* accept assignments from event callbacks; actual state comes from Bridge */ }
        }

        public SimpleMcpServer(Type commandSourceType) : this(commandSourceType, McpServerConfig.Load())
        {
        }

        public SimpleMcpServer(Type commandSourceType, McpServerConfig config)
        {
            //DisableServerHeader(); //Prob not needed
            _targetType = commandSourceType;
            _config = config ?? McpServerConfig.Load();
            
            string baseUrl = _config.GetBaseUrl();
            Console.WriteLine($"MCP server listening on {baseUrl}");
            Console.WriteLine($"Connect via Streamable HTTP: {_config.GetStreamableDisplayUrl()}");
            Console.WriteLine($"Legacy SSE endpoint: {_config.GetDisplayUrl()}");
            
            _listener.Prefixes.Add($"{baseUrl}sse/"); //Request come in without a trailing '/' but are still handled
            _listener.Prefixes.Add($"{baseUrl}message/");

            //_listener.Prefixes.Add("http://127.0.0.1:45000/sse/"); //Request come in without a trailing '/' but are still handled
            //_listener.Prefixes.Add("http://127.0.0.1:45000/message/");
            // Reflect and register [Command] methods
            foreach (var method in commandSourceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = method.GetCustomAttribute<CommandAttribute>();
                if (attr != null)
                    _commands[attr.Name] = method;
            }
        }

        public static void DisableServerHeader()
        {
            const string keyPath = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters";
            const string valueName = "DisableServerHeader";
            const int desiredValue = 2;

            try
            {
                using (var key = Registry.LocalMachine.CreateSubKey(keyPath, true))
                {
                    if (key == null)
                    {
                        Console.WriteLine("Failed to open or create the registry key.");
                        return;
                    }

                    var currentValue = key.GetValue(valueName);
                    if (currentValue == null || (int)currentValue != desiredValue)
                    {
                        key.SetValue(valueName, desiredValue, RegistryValueKind.DWord);
                        Console.WriteLine("Registry value updated. Restarting HTTP service...");

                        RestartHttpService();
                    }
                    else
                    {
                        Console.WriteLine("DisableServerHeader is already set to 2. No changes made.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error modifying registry: " + ex.Message);
            }
        }

        private static void RestartHttpService()
        {
            try
            {
                ExecuteCommand("net stop http");
                ExecuteCommand("net start http");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to restart HTTP service. Try rebooting manually. Error: " + ex.Message);
            }
        }

        private static void ExecuteCommand(string command)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    Verb = "runas", // Run as administrator
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            process.Start();
            process.WaitForExit();
        }

        private bool _isRunning = false;

        public bool IsRunning => _isRunning;

        public bool Start()
        {
            if (_isRunning)
            {
                Console.WriteLine("MCP server is already running.");
                return true;
            }

            try
            {
                _listener.Start();
                _listener.BeginGetContext(OnRequest, null);
                _isRunning = true;
                Console.WriteLine("MCP server started. CurrentlyDebugging: " + Bridge.DbgIsDebugging() + " IsRunning: " + Bridge.DbgIsRunning());
                return true;
            }
            catch (Exception ex)
            {
                _isRunning = false;
                Console.WriteLine("Failed to start MCP server: " + ex.Message);
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                bool wasRunning = _isRunning;
                _isRunning = false;

                if (_listener.IsListening)
                {
                    _listener.Stop();
                }

                lock (_sseSessions)
                {
                    foreach (var kv in _sseSessions)
                    {
                        try { kv.Value.Dispose(); } catch { }
                    }
                    _sseSessions.Clear();
                }

                _listener.Close();
                Console.WriteLine(wasRunning ? "MCP server stopped." : "MCP server was not running.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to stop MCP server: " + ex.Message);
            }
        }


    public static void PrettyPrintJson(string json) //x64Dbg does not support {}, remove them or it will crash
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string prettyJson = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var compact = string.Join(Environment.NewLine,
            prettyJson.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()));

            Console.WriteLine(compact.Replace("{", "").Replace("}", "").Replace("\r", ""));
        }
        catch (JsonException ex)
        {
            Console.WriteLine("Invalid JSON: " + ex.Message);
        }
    }

        static bool pDebug = false;
        private const string DefaultProtocolVersion = "2024-11-05";
        private static readonly Dictionary<string, StreamWriter> _sseSessions = new Dictionary<string, StreamWriter>();

        private static string CreateSessionId()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] randomBytes = new byte[16];
                rng.GetBytes(randomBytes);

                string base64String = Convert.ToBase64String(randomBytes);
                return base64String.TrimEnd('=').Replace('/', 'A').Replace('+', '-');
            }
        }

        private static string GetStreamableSessionId(HttpListenerRequest request)
        {
            return request.Headers["MCP-Session-Id"] ?? request.Headers["Mcp-Session-Id"];
        }

        private static void SendAcceptedResponse(HttpListenerContext ctx)
        {
            ctx.Response.StatusCode = 202;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
        }

        private static void SendJsonResponse(HttpListenerContext ctx, object payload, string sessionId = null, int statusCode = 200)
        {
            var json = new JavaScriptSerializer().Serialize(payload);
            var buffer = Encoding.UTF8.GetBytes(json);

            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentEncoding = Encoding.UTF8;

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                ctx.Response.Headers["MCP-Session-Id"] = sessionId;
            }

            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.Close();
        }

        private static void SendSsePayload(string sessionId, object payload, object requestId = null)
        {
            var sseData = new JavaScriptSerializer().Serialize(payload);

            lock (_sseSessions)
            {
                var writer = _sseSessions[sessionId];
                if (requestId != null)
                {
                    writer.Write($"id: {requestId}\n");
                }

                writer.Write($"data: {sseData}\n\n");
                writer.Flush();
            }
        }

        private static string GetRequestedProtocolVersion(Dictionary<string, object> json)
        {
            if (json != null &&
                json.TryGetValue("params", out var rawParams) &&
                rawParams is Dictionary<string, object> paramsDict &&
                paramsDict.TryGetValue("protocolVersion", out var rawProtocolVersion))
            {
                return rawProtocolVersion?.ToString();
            }

            return null;
        }

        private static string GetServerVersion()
        {
            return typeof(SimpleMcpServer).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        }

        private async void OnRequest(IAsyncResult ar) // Make async void for simplicity here, consider Task for robustness
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.EndGetContext(ar);
            }
            catch (ObjectDisposedException)
            {
                return; // shutting down
            }
            catch (HttpListenerException)
            {
                return; // listener stopped or aborted
            }
            catch (Exception)
            {
                return;
            }

            if (_isRunning && _listener.IsListening)
            {
                try { _listener.BeginGetContext(OnRequest, null); } catch { }
            }

            if (pDebug)
            {
                Console.WriteLine("=== Incoming Request ===");
                Console.WriteLine($"Method: {ctx.Request.HttpMethod}");
                Console.WriteLine($"URL: {ctx.Request.Url}");
                Console.WriteLine($"Headers:");
                foreach (string key in ctx.Request.Headers)
                {
                    Console.WriteLine($"  {key}: {ctx.Request.Headers[key]}");
                }
                Console.WriteLine("=========================");
            }
            string requestBody = null; // Variable to store the body
            ctx.Response.Headers["Server"] = "Kestrel";

            if (ctx.Request.HttpMethod == "POST")
            {
                var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                bool isLegacySsePost = path.StartsWith("/message") && !string.IsNullOrWhiteSpace(ctx.Request.QueryString["sessionId"]);
                bool isStreamableHttpPost = !isLegacySsePost &&
                    (path.StartsWith("/sse") || path.StartsWith("/message") || path.StartsWith("/mcp"));

                if (isLegacySsePost || isStreamableHttpPost)
                {
                    var query = ctx.Request.QueryString["sessionId"];
                    if (isLegacySsePost && (string.IsNullOrWhiteSpace(query) || !_sseSessions.ContainsKey(query)))
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.OutputStream.Close();
                        return;
                    }

                    using (var reader = new StreamReader(ctx.Request.InputStream))
                    {
                        var jsonBody = reader.ReadToEnd();

                        if (ctx.Request.HasEntityBody)
                        {
                            if (pDebug)
                            {
                                Console.WriteLine("Body:");
                                Debug.WriteLine("jsonBody:" + jsonBody);
                            }
                            //Console.WriteLine(jsonBody);
                        }
                        else
                        {
                            if (pDebug)
                            {
                                Console.WriteLine("No body.");
                            }
                        }

                        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(jsonBody);

                        if (!String.IsNullOrEmpty(jsonBody))
                        {
                            if (pDebug)
                            {
                                PrettyPrintJson(jsonBody);
                            }
                        }

                        string method = json["method"]?.ToString();
                        var @params = json.ContainsKey("params") ? json["params"] as object[] : null;
                        var requestId = json.ContainsKey("id") ? json["id"] : null;

                        if (method == "rpc.discover")
                        {
                            var toolList = new List<object>();
                            foreach (var cmd in _commands)
                            {
                                toolList.Add(new
                                {
                                    name = cmd.Key,
                                    parameters = new[] { "string[]" }
                                });
                            }

                            var response = new
                            {
                                jsonrpc = "2.0",
                                id = json["id"],
                                result = toolList
                            };

                            if (isStreamableHttpPost)
                            {
                                SendJsonResponse(ctx, response);
                            }
                            else
                            {
                                SendSsePayload(query, response, requestId);
                                SendAcceptedResponse(ctx);
                            }
                        }
                        else if (method == "ping")
                        {
                            var response = new
                            {
                                jsonrpc = "2.0",
                                id = requestId,
                                result = new { }
                            };

                            if (isStreamableHttpPost)
                            {
                                SendJsonResponse(ctx, response);
                            }
                            else
                            {
                                SendSsePayload(query, response, requestId);
                                SendAcceptedResponse(ctx);
                            }
                        }
                        else if (method == "initialize")
                        {
                            string protocolVersion = GetRequestedProtocolVersion(json);
                            if (string.IsNullOrWhiteSpace(protocolVersion))
                            {
                                protocolVersion = DefaultProtocolVersion;
                            }

                            var initializeResponse = new
                            {
                                jsonrpc = "2.0",
                                id = requestId,
                                result = new
                                {
                                    protocolVersion = protocolVersion,
                                    capabilities = new { tools = new { } },
                                    serverInfo = new { name = "x64DbgMCPServer", version = GetServerVersion() },
                                    instructions = ""
                                }
                            };

                            if (isStreamableHttpPost)
                            {
                                string sessionId = GetStreamableSessionId(ctx.Request);
                                if (string.IsNullOrWhiteSpace(sessionId))
                                {
                                    sessionId = CreateSessionId();
                                }

                                SendJsonResponse(ctx, initializeResponse, sessionId);
                            }
                            else
                            {
                                try
                                {
                                    SendSsePayload(query, initializeResponse);
                                    Debug.WriteLine("Responding with Session:" + query);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"SSE connection error: {ex.Message}");
                                }

                                SendAcceptedResponse(ctx);
                            }
                        }
                        else if (method == "notifications/initialized")
                        {
                            SendAcceptedResponse(ctx);
                        }
                        else if (method == "tools/list")
                        {
                            //POST /message?sessionId=nn-PaJBhGnUTSs8Wi9IYeA HTTP/1.1
                            //Host: localhost: 45000
                            //Content-Type: application/json; charset=utf-8
                            //Content-Length: 202
                            //{"jsonrpc":"2.0","id":"d95cc745587346b4bf7df2b13ec0890a-2","method":"tools/list"}
                            try
                            {
                                // Dynamically get all Command methods
                                var toolsList = new List<object>();

                                // Use _commands dictionary which should contain all registered commands
                                foreach (var command in _commands)
                                {
                                    string commandName = command.Key;
                                    MethodInfo methodInfo = command.Value;

                                    // Get the Command attribute to access its properties
                                    var attribute = methodInfo.GetCustomAttribute<CommandAttribute>();
                                    if (attribute != null && (!attribute.DebugOnly || Debugger.IsAttached || (Bridge.DbgIsDebugging() && Bridge.DbgValFromString("$pid") > 0 )))
                                    {
                                        // Get parameter info for the method
                                        var parameters = methodInfo.GetParameters();
                                        var properties = new Dictionary<string, object>();
                                        var required = new List<string>();

                                        foreach (var param in parameters)
                                        {
                                            string paramName = param.Name;
                                            string paramType = GetJsonSchemaType(param.ParameterType);
                                            string paramdescription = $"Parameter for {commandName}";
                                            switch (paramName) // Removed 'case' from here
                                            {
                                                case "address":
                                                    paramdescription = "Address to target with function (Example format: 0x12345678)";
                                                    break; // Added break
                                                case "value":
                                                    paramdescription = "value to pass to command (Example format: 100)";
                                                    break; // Added break
                                                case "byteCount":
                                                    paramdescription = "Count of how many bytes to request for (Example format: 100)";
                                                    break; // Added break
                                                case "pfilepath":
                                                    paramdescription = "File path (Example format: C:\\output.txt)";
                                                    break;
                                                case "mode":
                                                    paramdescription = "mode=[Comment | Label] (Example format: mode=Comment)";
                                                    break;
                                                case "byteString":
                                                    paramdescription = "Writes the provided Hex bytes .. .. (Example format: byteString=00 90 0F)";
                                                    break;
                                                default:
                                                    // No action needed here since the default description is already set above.
                                                    // The break is technically optional for the last section (default),
                                                    // but good practice to include.
                                                    break;
                                            }


                                            properties[paramName] = new
                                            {
                                                type = paramType,
                                                description = paramdescription
                                            };
                                                

                                            if (!param.IsOptional)
                                            {
                                                required.Add(paramName);
                                            }
                                        }

                                        // Create the tool definition
                                        object tool;
                                        if (attribute.MCPCmdDescription != null)
                                        {
                                            tool = new
                                            {
                                                name = commandName,
                                                description = attribute.MCPCmdDescription,
                                                inputSchema = new
                                                {
                                                    title = commandName,
                                                    description = attribute.MCPCmdDescription,
                                                    type = "object",
                                                    properties = properties,
                                                    required = required.ToArray()
                                                }
                                            };
                                        }
                                        else
                                        {                                        
                                            tool = new
                                            {
                                                name = commandName,
                                                description = $"Command: {commandName}",
                                                inputSchema = new
                                                {
                                                    title = commandName,
                                                    description = $"Command: {commandName}",
                                                    type = "object",
                                                    properties = properties,
                                                    required = required.ToArray()
                                                }
                                            };
                                        }
                                        toolsList.Add(tool);
                                    }
                                }

                                // Add the default tools for Diag
                                toolsList.Add(
                                    new
                                    {
                                        name = "Echo",
                                        description = "Echoes the input back to the client.",
                                        inputSchema = new
                                        {
                                            title = "Echo",
                                            description = "Echoes the input back to the client.",
                                            type = "object",
                                            properties = new
                                            {
                                                message = new
                                                {
                                                    type = "string"
                                                }
                                            },
                                            required = new[] { "message" }
                                        }
                                    }
                                );

                                var discoverResponse = new
                                {
                                    jsonrpc = "2.0",
                                    id = requestId,
                                    result = new
                                    {
                                        tools = toolsList.ToArray()
                                    }
                                };

                                if (isStreamableHttpPost)
                                {
                                    SendJsonResponse(ctx, discoverResponse);
                                }
                                else
                                {
                                    SendSsePayload(query, discoverResponse);
                                    Debug.WriteLine("Responding with Session:" + query);
                                    SendAcceptedResponse(ctx);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"SSE connection error: {ex.Message}");
                                if (isStreamableHttpPost)
                                {
                                    var errorResponse = new
                                    {
                                        jsonrpc = "2.0",
                                        id = requestId,
                                        error = new { code = -32603, message = ex.Message }
                                    };
                                    SendJsonResponse(ctx, errorResponse);
                                }
                                else
                                {
                                    var errorResponse = new
                                    {
                                        jsonrpc = "2.0",
                                        id = requestId,
                                        error = new { code = -32603, message = ex.Message }
                                    };
                                    SendSsePayload(query, errorResponse, requestId);
                                    SendAcceptedResponse(ctx);
                                }
                            }
                        }
                        else if (method == "tools/call")
                        {
                            //POST / message?sessionId=nn-PaJBhGnUTSs8Wi9IYeA HTTP / 1.1
                            //Host: localhost: 45000
                            //Content-Type: application/json; charset=utf-8
                            //Content-Length: 202
                            //{ "jsonrpc":"2.0","id":"d95cc745587346b4bf7df2b13ec0890a-3","method":"tools/call","params":{ "name":"Echo","arguments":{ "message":"tesrt"} } }
                            try
                            {
                                Debug.WriteLine("Params: " + json["params"]);

                                string toolName = null;
                                Dictionary<string, object> arguments = null;
                                string resultText = null;
                                bool isError = false;

                                try
                                {
                                    // JavaScriptSerializer likely returns Dictionary<string, object> 
                                    // rather than a strongly typed object, so use dictionary access
                                    var paramsDict = json["params"] as Dictionary<string, object>;
                                    if (paramsDict != null && paramsDict.ContainsKey("name"))
                                    {
                                        toolName = paramsDict["name"].ToString();

                                        if (paramsDict.ContainsKey("arguments"))
                                        {
                                            arguments = paramsDict["arguments"] as Dictionary<string, object>;
                                        }
                                    }

                                    if (toolName == null || arguments == null)
                                    {
                                        throw new ArgumentException("Invalid request format: missing name or arguments");
                                    }

                                    // Handle Echo command specially
                                    if (toolName == "Echo")
                                    {
                                        // Get the message using dictionary access
                                        if (arguments.ContainsKey("message"))
                                        {
                                            var message = arguments["message"]?.ToString();
                                            resultText = "hello " + message;
                                        }
                                        else
                                        {
                                            throw new ArgumentException("Echo command requires a 'message' argument");
                                        }
                                    }
                                    // Dynamically invoke registered commands
                                    else if (_commands.TryGetValue(toolName, out var methodInfo))
                                    {
                                        try
                                        {
                                            // Get parameter info for the method
                                            var parameters = methodInfo.GetParameters();
                                            var paramValues = new object[parameters.Length];

                                            // Build parameters for the method call
                                            for (int i = 0; i < parameters.Length; i++)
                                            {
                                                var param = parameters[i];
                                                var argName = param.Name;

                                                // Try to get the argument value using dictionary access
                                                if (arguments.ContainsKey(argName))
                                                {
                                                    var argValue = arguments[argName];

                                                    // Handle arrays specially
                                                    if (param.ParameterType.IsArray && argValue != null)
                                                    {
                                                        // If argValue is already an array or ArrayList
                                                        var argList = argValue as System.Collections.IList;
                                                        if (argList != null)
                                                        {
                                                            var elementType = param.ParameterType.GetElementType();
                                                            var typedArray = Array.CreateInstance(elementType, argList.Count);

                                                            for (int j = 0; j < argList.Count; j++)
                                                            {
                                                                var element = argList[j];

                                                                try
                                                                {
                                                                    // Convert element to the correct type
                                                                    var convertedValue = Convert.ChangeType(element, elementType);
                                                                    typedArray.SetValue(convertedValue, j);
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    throw new ArgumentException($"Cannot convert element at index {j} to type {elementType.Name}: {ex.Message}");
                                                                }
                                                            }

                                                            paramValues[i] = typedArray;
                                                        }
                                                        else
                                                        {
                                                            throw new ArgumentException($"Parameter '{argName}' should be an array");
                                                        }
                                                    }
                                                    else if (argValue != null)
                                                    {
                                                        try
                                                        {
                                                            // Convert single value to the correct type
                                                            paramValues[i] = Convert.ChangeType(argValue, param.ParameterType);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            throw new ArgumentException($"Cannot convert parameter '{argName}' to type {param.ParameterType.Name}: {ex.Message}");
                                                        }
                                                    }
                                                }
                                                else if (param.IsOptional)
                                                {
                                                    // Use default value for optional parameters
                                                    paramValues[i] = param.DefaultValue;
                                                }
                                                else
                                                {
                                                    // Missing required parameter
                                                    throw new ArgumentException($"Required parameter '{argName}' is missing");
                                                }
                                            }

                                            // Invoke the method
                                            var result = methodInfo.Invoke(null, paramValues);

                                            // Convert result to string
                                            resultText = result?.ToString() ?? "Command executed successfully";
                                        }
                                        catch (Exception ex)
                                        {
                                            resultText = $"Error executing command: {ex.Message}";
                                            isError = true;
                                        }
                                    }
                                    else
                                    {
                                        resultText = $"Command '{toolName}' not found";
                                        isError = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    resultText = $"Error processing command: {ex.Message}";
                                    isError = true;
                                }

                                var responsePayload = new
                                {
                                    jsonrpc = "2.0",
                                    id = requestId,
                                    result = new
                                    {
                                        content = new object[] {
                                            new {
                                                type = "text",
                                                text = resultText
                                            }
                                        },
                                        isError = isError
                                    }
                                };

                                if (isStreamableHttpPost)
                                {
                                    SendJsonResponse(ctx, responsePayload);
                                }
                                else
                                {
                                    SendSsePayload(query, responsePayload);
                                    Debug.WriteLine("Responding with Session:" + query);
                                    SendAcceptedResponse(ctx);
                                }
                            }
                            catch (Exception ex)
                            {
                                // Handle general errors
                                var errorPayload = new
                                {
                                    jsonrpc = "2.0",
                                    id = requestId,
                                    result = new
                                    {
                                        content = new object[] {
                                        new {
                                            type = "text",
                                            text = $"Error processing request: {ex.Message}"
                                        }
                                    },
                                        isError = true
                                    }
                                };

                                if (isStreamableHttpPost)
                                {
                                    SendJsonResponse(ctx, errorPayload);
                                }
                                else
                                {
                                    SendSsePayload(query, errorPayload);
                                    SendAcceptedResponse(ctx);
                                }
                                Console.WriteLine($"Error processing tools/call: {ex.Message}");
                            }
                        }
                        else if (_commands.TryGetValue(method, out var methodInfo))
                        {
                            try
                            {
                                string[] args = Array.ConvertAll(@params ?? new object[0], p => p?.ToString() ?? "");
                                var result = methodInfo.Invoke(null, new object[] { args });

                                var response = new
                                {
                                    jsonrpc = "2.0",
                                    id = json["id"],
                                    result = result
                                };

                                if (isStreamableHttpPost)
                                {
                                    SendJsonResponse(ctx, response);
                                }
                                else
                                {
                                    SendSsePayload(query, response, requestId);
                                    SendAcceptedResponse(ctx);
                                }
                            }
                            catch (Exception ex)
                            {
                                var response = new
                                {
                                    jsonrpc = "2.0",
                                    id = json["id"],
                                    error = new { code = -32603, message = ex.Message }
                                };

                                if (isStreamableHttpPost)
                                {
                                    SendJsonResponse(ctx, response);
                                }
                                else
                                {
                                    SendSsePayload(query, response, requestId);
                                    SendAcceptedResponse(ctx);
                                }
                            }
                        }
                        else
                        {
                            var response = new
                            {
                                jsonrpc = "2.0",
                                id = json["id"],
                                error = new { code = -32601, message = "Unknown method" }
                            };

                            if (isStreamableHttpPost)
                            {
                                SendJsonResponse(ctx, response);
                            }
                            else
                            {
                                SendSsePayload(query, response, requestId);
                                SendAcceptedResponse(ctx);
                            }
                        }
                    }
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }

            if (ctx.Request.HttpMethod == "GET")
            {
                var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                if (path.EndsWith("/discover") || path.EndsWith("/mcp/"))
                {
                    var toolList = new List<object>();

                    foreach (var cmd in _commands)
                    {
                        toolList.Add(new
                        {
                            name = cmd.Key,
                            parameters = new[] { "string[]" }
                        });
                    }

                    var json = new JavaScriptSerializer().Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = (string)null,
                        result = toolList
                    });

                    var buffer = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = buffer.Length;
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    ctx.Response.Close();
                }
                else if (path.EndsWith("/sse/") || path.EndsWith("/sse") || path.EndsWith("/mcp/") || path.EndsWith("/mcp"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.StatusCode = 200;
                    ctx.Response.SendChunked = true;
                    ctx.Response.Headers.Add("Cache-Control", "no-store");

                    string sessionId = GetStreamableSessionId(ctx.Request);
                    bool isLegacySseHandshake = string.IsNullOrWhiteSpace(sessionId);
                    if (isLegacySseHandshake)
                    {
                        sessionId = CreateSessionId();
                    }

                    var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                    lock (_sseSessions)
                    {
                        if (_sseSessions.TryGetValue(sessionId, out var existingWriter))
                        {
                            try { existingWriter.Dispose(); } catch { }
                        }

                        _sseSessions[sessionId] = writer;
                    }

                    if (isLegacySseHandshake)
                    {
                        writer.Write("event: endpoint\n");
                        writer.Write($"data: /message?sessionId={sessionId}\n\n");
                    }
                    else
                    {
                        writer.Write(":\n\n");
                    }
                    writer.Flush();
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        }





        // Helper method to convert C# types to JSON schema types
        private string GetJsonSchemaType(Type type)
        {
            if (type == typeof(string))
                return "string";
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                     type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort))
                return "integer";
            else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";
            else if (type == typeof(bool))
                return "boolean";
            else if (type.IsArray)
                return "array";
            else
                return "object";
        }



       
    }
}
