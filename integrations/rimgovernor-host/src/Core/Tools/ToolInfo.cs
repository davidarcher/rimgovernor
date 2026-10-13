using System;
using System.Collections.Generic;

namespace RimGovernor.Host.Core.Tools
{
    /// <summary>
    /// Information about a registered tool
    /// </summary>
    public class ToolInfo
    {
        /// <summary>
        /// Tool name (e.g., "inventory/get")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Human-readable title
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Human-readable summary of what a successful result means or what useful handle it returns.
        /// </summary>
        public string ResultDescription { get; set; }

        /// <summary>
        /// Optional machine-readable classification tags for bridge clients.
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Parameter information
        /// </summary>
        public List<ToolParameterInfo> Parameters { get; set; } = new List<ToolParameterInfo>();

        /// <summary>
        /// Response field documentation
        /// </summary>
        public List<ToolResponseFieldInfo> ResponseFields { get; set; } = new List<ToolResponseFieldInfo>();

        /// <summary>
        /// Whether authentication is required
        /// </summary>
        public bool RequiresAuth { get; set; } = true;
    }

    /// <summary>
    /// Information about a field in the tool response
    /// </summary>
    public class ToolResponseFieldInfo
    {
        /// <summary>
        /// Field name in the JSON response
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// JSON Schema type (string, boolean, integer, number, object, array)
        /// </summary>
        public string Type { get; set; } = "string";

        /// <summary>
        /// Human-readable description
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Whether this field is always present
        /// </summary>
        public bool Always { get; set; } = true;

        /// <summary>
        /// Whether this field can be null
        /// </summary>
        public bool Nullable { get; set; } = false;
    }

    /// <summary>
    /// Information about a tool parameter
    /// </summary>
    public class ToolParameterInfo
    {
        /// <summary>
        /// Parameter name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Parameter type
        /// </summary>
        public Type Type { get; set; } = typeof(object);

        /// <summary>
        /// Human-readable description
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Whether the parameter is required
        /// </summary>
        public bool Required { get; set; } = true;

        /// <summary>
        /// Default value if not provided
        /// </summary>
        public object DefaultValue { get; set; }
    }
}
