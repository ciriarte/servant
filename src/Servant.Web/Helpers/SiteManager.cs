﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.Administration;
using Servant.Business.Helpers;
using Servant.Business.Objects;
using Servant.Business.Objects.Enums;
using Binding = Servant.Business.Objects.Binding;
using CreateSiteResult = Servant.Business.Objects.Enums.CreateSiteResult;
using Site = Servant.Business.Objects.Site;

namespace Servant.Web.Helpers
{
    public static class SiteManager
    {
        static SiteManager()
        {
            using (var manager = new ServerManager())
            {
                try
                {
                    var testForIis = manager.Sites.FirstOrDefault();
                    var testForIisExpress = manager.WorkerProcesses.FirstOrDefault();
                }
                catch (COMException)
                {
                    throw new Exception("Looks like IIS is not installed.");
                }

                catch (NotImplementedException)
                {
                    throw new Exception("Servant doesn't support IIS Express.");
                }
            }

        }

        public static IEnumerable<Site> GetSites()
        {
            using (var manager = new ServerManager())
            {
                foreach (var site in manager.Sites)
                {
                    var parsedSite = ParseSite(site);
                    if (parsedSite != null)
                        yield return parsedSite;
                }    
            }
        }

        public static Microsoft.Web.Administration.Site GetIisSiteById(int iisId)
        {
            using (var manager = new ServerManager())
            {
                return manager.Sites.SingleOrDefault(x => x.Id == iisId);
            }
        }

        public static Servant.Business.Objects.Site GetSiteById(int iisId) 
        {
            using (var manager = new ServerManager())
            {
                var iisSite = manager.Sites.SingleOrDefault(x => x.Id == iisId);

                return iisSite == null
                    ? null
                    : ParseSite(iisSite);    
            }
        }

        private static Servant.Business.Objects.Site ParseSite(Microsoft.Web.Administration.Site site)
        {
            if (site == null)
                return null;

            ObjectState applicationPoolState;
            using (var manager = new ServerManager())
            {
                applicationPoolState = manager.ApplicationPools[site.Applications[0].ApplicationPoolName].State;    
            }

            var servantSite = new Site {
                    IisId = (int)site.Id,
                    Name = site.Name,
                    ApplicationPool = site.Applications[0].ApplicationPoolName,
                    SitePath = site.Applications[0].VirtualDirectories[0].PhysicalPath,
                    SiteState = (InstanceState)Enum.Parse(typeof(InstanceState), site.State.ToString()),
                    ApplicationPoolState = (InstanceState)Enum.Parse(typeof(InstanceState),  applicationPoolState.ToString()),
                    LogFileDirectory = site.LogFile.Directory,
                    Bindings = GetBindings(site).ToList(),
                };

            if (site.Applications.Count > 1)
            {
                foreach (var application in site.Applications.Skip(1))
                {
                    servantSite.Applications.Add(new SiteApplication
                        {
                            ApplicationPool = application.ApplicationPoolName,
                            Path = application.Path,
                            DiskPath = application.VirtualDirectories[0].PhysicalPath,
                        });
                }
            }

            return servantSite;
        }

        private static IEnumerable<Binding> GetBindings(Microsoft.Web.Administration.Site iisSite)
        {
            var allowedProtocols = new[] { "http", "https" };
            var certificates = GetCertificates();
            
            foreach (var binding in iisSite.Bindings.Where(x => allowedProtocols.Contains(x.Protocol)))
            {
                var servantBinding = new Binding();

                if (binding.Protocol == "https")
                {
                    if(binding.CertificateHash == null)
                        continue;

                    var certificate = certificates.SingleOrDefault(cert => cert.GetCertHash().SequenceEqual(binding.CertificateHash));
                    if (certificate != null)
                    {
                        servantBinding.CertificateName = certificate.FriendlyName;
                        servantBinding.CertificateHash = binding.CertificateHash;
                    }
                    else
                        continue;
                }
                servantBinding.Protocol = (Protocol) Enum.Parse(typeof(Protocol), binding.Protocol);
                servantBinding.Hostname = binding.Host;
                servantBinding.Port = binding.EndPoint.Port;
                var endPointAddress = binding.EndPoint.Address.ToString();
                servantBinding.IpAddress = endPointAddress == "0.0.0.0" ? "*" : endPointAddress;

                yield return servantBinding;
            }
        }

        public static List<X509Certificate2> GetCertificates()
        {
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.OpenExistingOnly);
            return store.Certificates.Cast<X509Certificate2>().Where(x => !string.IsNullOrWhiteSpace(x.FriendlyName)).ToList();
        }

        public static string GetSitename(Servant.Business.Objects.Site site) {
            if(site == null)
                return "Unknown";
            
            return site.Name;
        }

        private static IEnumerable<string> ConvertBindingsToBindingInformations(IEnumerable<Binding> bindings)
        {
            var bindingsToAdd = bindings
                .Select(binding => string.Format("*:{0}:{1}", binding.Port, binding.Hostname))
                .ToList();

            return bindingsToAdd.Distinct();
        }

        public static void UpdateSite(Servant.Business.Objects.Site site)
        {
            using (var manager = new ServerManager())
            {
                var iisSite = manager.Sites.SingleOrDefault(x => x.Id == site.IisId);
                var mainApplication = iisSite.Applications.First();

                mainApplication.VirtualDirectories[0].PhysicalPath = site.SitePath;
                iisSite.Name = site.Name;
                mainApplication.ApplicationPoolName = site.ApplicationPool;

                // Commits bindings
                iisSite.Bindings.Clear();
                foreach (var binding in site.Bindings)
                {
                    if (binding.Protocol == Protocol.https)
                        iisSite.Bindings.Add(binding.ToIisBindingInformation(), binding.CertificateHash, "My");
                    else
                        iisSite.Bindings.Add(binding.ToIisBindingInformation(), binding.Protocol.ToString());
                }

                //Intelligently updates virtual applications
                foreach (var application in site.Applications)
                {
                    var iisApp = iisSite.Applications.SingleOrDefault(x => x.Path == application.Path);

                    if (iisApp == null)
                    {
                        if (!application.Path.StartsWith("/"))
                            application.Path = "/" + application.Path;

                        iisSite.Applications.Add(application.Path, application.DiskPath);
                        iisApp = iisSite.Applications.Single(x => x.Path == application.Path);

                    }

                    iisApp.VirtualDirectories[0].PhysicalPath = application.DiskPath;
                    iisApp.ApplicationPoolName = application.ApplicationPool;
                }

                var applicationsToDelete = iisSite.Applications.Skip(1).Where(x => !site.Applications.Select(a => a.Path).Contains(x.Path));
                foreach (var application in applicationsToDelete)
                {
                    application.Delete();
                }
                
                manager.CommitChanges();
            }
        }

        public static string[] GetApplicationPools()
        {
            using (var manager = new ServerManager())
            {
                return manager.ApplicationPools.Select(x => x.Name).OrderBy(x => x).ToArray();    
            }
        }

        public static Servant.Business.Objects.Enums.SiteStartResult StartSite(Servant.Business.Objects.Site site)
        {
            using (var manager = new ServerManager())
            {
                var iisSite = manager.Sites.SingleOrDefault(x => x.Id == site.IisId);
                if (iisSite == null)
                    throw new SiteNotFoundException("Site " + site.Name + " was not found on IIS");

                try
                {
                    iisSite.Start();
                    return SiteStartResult.Started;
                }
                catch (Microsoft.Web.Administration.ServerManagerException)
                {
                    return SiteStartResult.BindingIsAlreadyInUse;
                }
                catch (FileLoadException)
                {
                    return SiteStartResult.CannotAccessSitePath;
                }
            }
        }

        public static void StopSite(Servant.Business.Objects.Site site)
        {
            using (var manager = new ServerManager())
            {
                var iisSite = manager.Sites.SingleOrDefault(x => x.Id == site.IisId);
                if (iisSite == null)
                    throw new SiteNotFoundException("Site " + site.Name + " was not found on IIS");

                iisSite.Stop();    
            }
        }

        public class SiteNotFoundException : Exception
        {
            public SiteNotFoundException(string message) : base(message) {}
        }

        public static Site GetSiteByName(string name)
        {
            using (var manager = new ServerManager())
            {
                return ParseSite(manager.Sites.SingleOrDefault(x => x.Name == name));    
            }
        }

        public static bool IsBindingInUse(string rawBinding, string ipAddress, int iisSiteId = 0)
        {
            var binding = BindingHelper.ConvertToBinding(BindingHelper.FinializeBinding(rawBinding), ipAddress);
            return IsBindingInUse(binding, iisSiteId);
        }

        public static bool IsBindingInUse(Binding binding, int iisSiteId = 0)
        {
            var bindingInformations = ConvertBindingsToBindingInformations(new[] {binding});
            return GetBindingInUse(iisSiteId, bindingInformations) != null;
        }

        public static Business.Objects.CreateSiteResult CreateSite(Site site)
        {
            var result = new Business.Objects.CreateSiteResult();


            var bindingInformations = site.Bindings.Select(x => x.ToIisBindingInformation()).ToList();

            // Check bindings
            var bindingInUse = GetBindingInUse(0, bindingInformations); // 0 never exists
            if (bindingInUse != null)
            {
                result.Result = CreateSiteResult.BindingAlreadyInUse;
                return result;
            }

            using (var manager = new ServerManager())
            {
                // Create site
                manager.Sites.Add(site.Name, "http", bindingInformations.First(), site.SitePath);
                var iisSite = manager.Sites.SingleOrDefault(x => x.Name == site.Name);

                // Add bindings
                iisSite.Bindings.Clear();
                foreach (var binding in bindingInformations)
                    iisSite.Bindings.Add(binding, "http");

                // Set/create application pool
                if (string.IsNullOrWhiteSpace(site.ApplicationPool)) // Auto create application pool
                {
                    var appPoolName = site.Name;
                    var existingApplicationPoolNames = manager.ApplicationPools.Select(x => x.Name).ToList();
                    var newNameCount = 1;

                    while (existingApplicationPoolNames.Contains(appPoolName))
                    {
                        appPoolName = site.Name + "_" + newNameCount;
                        newNameCount++;
                    }

                    manager.ApplicationPools.Add(appPoolName);
                    iisSite.ApplicationDefaults.ApplicationPoolName = appPoolName;
                }
                else
                {
                    iisSite.ApplicationDefaults.ApplicationPoolName = site.ApplicationPool;
                }

                manager.CommitChanges();

                var created = false;
                var sw = new Stopwatch();
                sw.Start();
                while (!created || sw.Elapsed.TotalSeconds >= 3)
                {
                    try
                    {
                        if (iisSite.State == ObjectState.Started)
                            created = true;
                    }
                    catch (COMException)
                    {
                        System.Threading.Thread.Sleep(100);
                    }

                }
                sw.Stop();

                if (created)
                {
                    result.Result = CreateSiteResult.Success;
                    result.IisSiteId = (int) iisSite.Id;
                }
                else
                {
                    result.Result = CreateSiteResult.Failed;
                }

                return result;
            }
        }

        private static string GetBindingInUse(int iisId, IEnumerable<string> bindingInformations)
        {
            using (var manager = new ServerManager())
            {
                var sites = manager.Sites.Where(x => x.Id != iisId);
                foreach (var iisSite in sites)
                    foreach (var binding in iisSite.Bindings)
                        if (bindingInformations.Contains(binding.BindingInformation))
                            return binding.BindingInformation;

                return null;    
            }
        }

        public static void RestartSite(int iisSiteId)
        {
            var site = GetSiteById(iisSiteId);
            StopSite(site);
            StartSite(site);
        }

        public static void RecycleApplicationPoolBySite(int iisSiteId)
        {
            var site = GetIisSiteById(iisSiteId);
            using (var manager = new ServerManager())
            {
                manager.ApplicationPools[site.Applications[0].ApplicationPoolName].Recycle();    
            }
        }

        public static void DeleteSite(int iisId)
        {
            using (var manager = new ServerManager())
            {
                var siteToDelete = manager.Sites.SingleOrDefault(x => x.Id == iisId);
                var applicationPoolname = siteToDelete.Applications[0].ApplicationPoolName;

                var sitesWithApplicationPoolname =
                    from site in manager.Sites
                    let application = site.Applications[0]
                    where application.ApplicationPoolName == applicationPoolname
                    select site;

                siteToDelete.Delete();

                if (sitesWithApplicationPoolname.Count() == 1)
                    manager.ApplicationPools[applicationPoolname].Delete();

                manager.CommitChanges();
            }

            System.Threading.Thread.Sleep(500);
        }
    }
}