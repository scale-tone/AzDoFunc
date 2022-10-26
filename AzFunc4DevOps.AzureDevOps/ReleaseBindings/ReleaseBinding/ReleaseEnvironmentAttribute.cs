using System;
using Microsoft.Azure.WebJobs.Description;

namespace AzFunc4DevOps.AzureDevOps 
{
    [Binding]    
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
    public class ReleaseEnvironmentAttribute : Attribute
    {
        [AutoResolve]
        public string ProjectName { get; set; }
    }
}