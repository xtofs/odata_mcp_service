# OData MCP Server

A Model Context Protocol (MCP) server that creates tools for counting entities in OData services.

## Overview

This MCP server connects to an OData service, reads its metadata, and automatically creates MCP tools for counting the number of entities in each entity set. It uses the official .NET MCP SDK and Microsoft OData libraries.

## Features

- **Automatic Tool Generation**: Creates two MCP tools per entity set found in the OData metadata
- **Entity Counting**: Count tools return the number of entities in each entity set using `/$count`
- **Entity Retrieval**: Get tools retrieve entities with optional `top` parameter (default: 10, max: 100)
- **OData V4 Support**: Works with OData V4 services using standard OData query conventions
- **Stdio Transport**: Uses standard input/output for MCP communication
- **Structured Logging**: Uses Serilog with hourly rotating log files stored at `D:\xtofs\odata_mcp_service\logs`

## Prerequisites

- .NET 10.0 or later
- Internet access to fetch OData metadata

## Usage

### Command Line

```bash
dotnet run -- <odata-metadata-url>
```

## getting started

Public available OData services
    - [Jetsons](https://jetsons.azurewebsites.net/$metadata) `https://jetsons.azurewebsites.net`
    - [Northwind](https://services.odata.org/V4/Northwind/Northwind.svc/$metadata) `https://services.odata.org/V4/Northwind/Northwind.svc`

### using vscode github copilot (agent mode)

Create an empty folder and add a file under
./.vscode/mcp.json

with the content

```JSON
{
  "servers": {
    "jetsons": {
      "command": "<project directory>/ODataMcpService/bin/Debug/net10.0/ODataMcpService.exe",
      "args": [
        "https://jetsons.azurewebsites.net",
      ]
    },
    "northwind": {
      "command": "<project directory>/ODataMcpService/bin/Debug/net10.0/ODataMcpService.exe",
      "args": [
        "https://services.odata.org/V4/Northwind/Northwind.svc",
      ]
    }
  }
}
```
