using System;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>
    /// Declares a transition's catalog metadata on its implementation class. The catalog is
    /// built by discovering classes carrying this attribute — no separate registration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TransitionAttribute : Attribute
    {
        public new string TypeId { get; }
        public string DisplayName { get; }
        public string Icon { get; set; } = "blend";
        public string Description { get; set; } = "";
        public double DefaultDurationSec { get; set; } = 0.5;

        public TransitionAttribute(string typeId, string displayName)
        {
            TypeId = typeId;
            DisplayName = displayName;
        }
    }

    /// <summary>Declares one parameter on a transition's implementation class.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class TransitionParamAttribute : Attribute
    {
        public string Key { get; }
        public string Label { get; }
        public ParamKind Kind { get; set; } = ParamKind.Double;
        public double Default { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public string[] Choices { get; set; } = Array.Empty<string>();

        public TransitionParamAttribute(string key, string label)
        {
            Key = key;
            Label = label;
        }
    }
}
