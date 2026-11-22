using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

namespace AspNetCoreVerifiableCredentials
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            //services.Configure<AppSettingsModel>(Configuration.GetSection("AppSettings"));


            services.AddDistributedMemoryCache();
            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(1);//You can set Time   
                options.Cookie.IsEssential = true;
                options.Cookie.HttpOnly = true;
            } );
            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => false;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddRazorPages();
            
services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

            services.AddHttpClient();  // use iHttpFactory as best practice, should be easy to use extra retry and hold off policies in the future
         services.AddScoped<AyaEmployeeClaimsService>();
            services.AddSingleton<AyaCredentialPolicy>();
            services.AddSingleton<AyaEmployeeRegistry>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IAyaRequestPersistence>(_ => {
                var path = Configuration["Aya:RequestStorePath"];
                return string.IsNullOrWhiteSpace(path) ? new UnconfiguredAyaRequestPersistence() : new FileAyaRequestPersistence(path);
            });
            services.AddSingleton<AyaIssuanceStateStore>();
            services.AddSingleton<AyaRequestConfiguration>();
            services.AddScoped<IVerifiedIdTokenProvider, VerifiedIdTokenProvider>();
             } 


        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseForwardedHeaders( new ForwardedHeadersOptions {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
            } );

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.Use(async (context, next) => {
                try { await next(); }
                catch (AyaStorageException) {
                    context.Response.StatusCode = 503;
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsJsonAsync(new { error = "request_storage_unavailable" });
                }
            });
            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseSession();
            // The Aya browser flows use same-origin requests; callbacks are server-to-server.

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapRazorPages();
            });

        }
    }
}
