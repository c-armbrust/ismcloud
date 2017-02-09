﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;
using IsmIoTPortal.Models;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Azure.KeyVault;
using System.Threading.Tasks;
using System.Configuration;
using IsmIoTSettings;
using System.Text;
using Microsoft.Azure.Devices;
using Microsoft.WindowsAzure;

namespace IsmIoTPortal.Controllers
{
    [IsmAuthorize(Groups ="Admins, DeviceAdmins")]
    public class SoftwareController : Controller
    {
        private static readonly ServiceClient serviceClient = ServiceClient.CreateFromConnectionString(ConfigurationManager.ConnectionStrings["ismiothub"].ConnectionString);
        private static KeyVaultClient kv = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(GetToken));
        private IsmIoTPortalContext db = new IsmIoTPortalContext();

        // GET: Software
        public ActionResult Index()
        {
            return View(db.Software.ToList());
        }

        // GET: Software/Details/5
        public ActionResult Details(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Software software = db.Software.Find(id);
            if (software == null)
            {
                return HttpNotFound();
            }
            return View(software);
        }

        // GET: Software/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: Software/Create
        // Aktivieren Sie zum Schutz vor übermäßigem Senden von Angriffen die spezifischen Eigenschaften, mit denen eine Bindung erfolgen soll. Weitere Informationen 
        // finden Sie unter http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<ActionResult> Create([Bind(Include = "SoftwareId,SoftwareVersion,Changelog")] Software software, HttpPostedFileBase upload)
        {
            if (ModelState.IsValid)
            {
                if (upload != null && upload.ContentLength > 0)
                {
                    bool error = false;
                    // If the uploaded file is not a tarfile, return with error
                    if (!Path.GetExtension(upload.FileName).Equals(".tar"))
                    {
                        ViewBag.FileError = "Uploaded file must be tarfile packed with update data and a script named 'apply.sh'";
                        error = true;
                    }
                    // If the software version already exists
                    if (db.Software.Any(s => s.SoftwareVersion.ToLower().Equals(software.SoftwareVersion.ToLower())))
                    {
                        ViewBag.NameError = "This software version already exists.";
                        error = true;
                    }
                    if (error)
                        return Task.Factory.StartNew<ActionResult>(
                          () => {
                              return View("Create");
                          });
                    try
                    {
                        software.Status = "Uploaded";
                        software.Author = "SWT";
                        software.Date = DateTime.Now;
                        // Add to database
                        db.Software.Add(software);
                        db.SaveChanges();

                        var location = Server.MapPath("~/sw-updates/" + software.SoftwareVersion);
                        PortalUtils.CreateNewFirmwareUpdateTask(upload, location, software.SoftwareId);
                        return Task.Factory.StartNew<ActionResult>(
                          () => {
                              return RedirectToAction("Index");
                          });
                        //return RedirectToAction("Index");
                    }
                    catch (Exception e)
                    {

                    }
                }
            }
            
            return Task.Factory.StartNew<ActionResult>(
              () => {
                  return View(software);
              });
        }

        // GET: Software/Edit/5
        public ActionResult Edit(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Software software = db.Software.Find(id);
            if (software == null)
            {
                return HttpNotFound();
            }
            return View(software);
        }

        // POST: Software/Edit/5
        // Aktivieren Sie zum Schutz vor übermäßigem Senden von Angriffen die spezifischen Eigenschaften, mit denen eine Bindung erfolgen soll. Weitere Informationen 
        // finden Sie unter http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit([Bind(Include = "SoftwareId,SoftwareVersion")] Software software)
        {
            if (ModelState.IsValid)
            {
                db.Entry(software).State = EntityState.Modified;
                db.SaveChanges();
                return RedirectToAction("Index");
            }
            return View(software);
        }

        // GET: Software/Delete/5
        public ActionResult Delete(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Software software = db.Software.Find(id);
            if (software == null)
            {
                return HttpNotFound();
            }
            return View(software);
        }

        // POST: Software/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteConfirmed(int id)
        {
            Software software = db.Software.Find(id);
            db.Software.Remove(software);
            db.SaveChanges();
            return RedirectToAction("Index");
        }

        [AllowAnonymous]
        public async Task<ActionResult> GetKey()
        {
            // Get key
            var key = await kv.GetKeyAsync(ConfigurationManager.AppSettings["kv:fw-signing-key"]);
            // Get pem formatted public key string
            var pubKey = PortalUtils.GetPublicKey(key.Key);
            // Convert string to stream
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(pubKey));
            // Send stream to file download
            return File(stream, MimeMapping.GetMimeMapping("public.pem"), "public.pem");
        }

        public ActionResult Rollout(int? id)
        {
            if (id == null)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            Software software = db.Software.Find(id);
            if (software == null)
                return HttpNotFound();

            // Get all devices that already have this software version
            var updatedDevices = software.IsmDevices;
            // We create a separate ID collection because Entity Framework doesn't support
            // Conversion from LINQ to Entity Queries with Objects, since there is no SQL 
            // Equality comparator for <IsmDevice>
            var updatedDevicesIds = updatedDevices.Select(d => d.IsmDeviceId);
            // Get all the devices that don't have this software version
            var devices = db.IsmDevices.Where(
                d => !updatedDevicesIds.Contains(d.IsmDeviceId)
                );

            return View(new SoftwareView
            {
                Software = software,
                Devices = devices.ToList()
            });
        }

        // POST: Software/Rollout/5
        // Aktivieren Sie zum Schutz vor übermäßigem Senden von Angriffen die spezifischen Eigenschaften, mit denen eine Bindung erfolgen soll. Weitere Informationen 
        // finden Sie unter http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Rollout(int? id, int[] selectedDevices)
        {
            if (id == null)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            Software software = db.Software.Find(id);
            if (software == null)
                return HttpNotFound();

            foreach (var deviceId in selectedDevices)
            {
                var device = db.IsmDevices.Find(deviceId);
                if (device == null)
                    continue;
                // If the device's software version is newer than the one we'll be updating to, skip this device
                if (device.Software.Date > software.Date)
                    continue;
                // Roll out update async
                PortalUtils.RolloutFwUpdateAsync(device.DeviceId, serviceClient, software.Url);

            }

            return RedirectToAction("Index");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                db.Dispose();
            }
            base.Dispose(disposing);
        }

        private static async Task<string> GetToken(string authority, string resource, string scope)
        {
            var id = ConfigurationManager.AppSettings["hsma-ida:PortalClientId"];
            var secret = ConfigurationManager.AppSettings["hsma-ida:PortalAppKey"];
            return await IsmIoTSettings.IsmUtils.GetAccessToken(authority, resource, id, secret);
        }
    }
}
