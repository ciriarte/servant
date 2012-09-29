﻿using System.Collections.Generic;
using System.Dynamic;
using Nancy.Security;
using Servant.Manager.Objects;
using Nancy;
using Servant.Manager.Views.Shared.Models;

namespace Servant.Manager.Modules
{
    public class BaseModule : NancyModule 
    {
        public dynamic Model = new ExpandoObject();
        protected PageModel Page { get; set; }
        public bool HasErrors { get { return Model.Errors.Count != 0; }}

        public BaseModule()
        {
            SetupModelDefaults();
            this.RequiresAuthentication();
        }

        public BaseModule(string modulePath) : base(modulePath)
        {
            SetupModelDefaults();
        }
            
        public void SetupModelDefaults() 
        {
            Before += ctx =>
            {
                Page = new PageModel
                {
                    Servername = System.Environment.MachineName
                };

                Model.Page = Page;
                Model.Errors = new List<Error>();
                return null;
            };

            After += ctx =>
            {
                Model.ErrorsAsJson = new Nancy.Json.JavaScriptSerializer().Serialize(Model.Errors);
            };
        }

        public void AddGlobalError(string message)
        {
            Model.Errors.Add(new Error { IsGlobal = true, Message = message});
        }

        public void AddPropertyError(string propertyName, string message)
        {
            Model.Errors.Add(new Error { IsGlobal = false, PropertyName = propertyName, Message = message });
        }
    }
}