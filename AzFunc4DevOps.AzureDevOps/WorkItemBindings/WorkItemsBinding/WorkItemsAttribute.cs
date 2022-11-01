using System;
using Microsoft.Azure.WebJobs.Description;

namespace AzFunc4DevOps.AzureDevOps 
{
    [Binding]    
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
    public class WorkItemsAttribute : Attribute
    {
        [AutoResolve]
        public string ProjectName { get; set; }

        [AutoResolve]
        public string WiqlQueryWhereClause { get; set; }
    }
}