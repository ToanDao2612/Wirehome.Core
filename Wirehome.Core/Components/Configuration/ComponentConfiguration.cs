﻿
namespace Wirehome.Core.Components.Configuration
{
    public class ComponentConfiguration
    {
        public bool IsEnabled { get; set; } = true;

        public string Description { get; set; }
        
        public bool DelayedStart { get; set; }

        public ComponentLogicConfiguration Logic { get; set; }
    }
}
