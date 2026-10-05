using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SistemaChotaExpress.Data;
using SistemaChotaExpress.Models;
using SistemaChotaExpress.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.All;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.HttpOnly = true;
});

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new ResponseCacheAttribute
    {
        NoStore = true,
        Location = ResponseCacheLocation.None
    });
});

string finalConn =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL")
    ?? builder.Configuration.GetConnectionString("CadenaChotaExpress")
    ?? "";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(finalConn))
    {
        options.UseSqlServer(finalConn);
    }
});

builder.Services.AddHttpClient();
builder.Services.AddScoped<IDocumentLookupService, DocumentLookupService>();
builder.Services.AddScoped<SunatService>();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login/Login";
        options.AccessDeniedPath = "/Login/AccesoDenegado";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Login}/{action=Login}/{id?}");

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();

        context.Database.EnsureCreated();

        try
        {
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Encomiendas' AND xtype='U')
                CREATE TABLE [Encomiendas] (
                    [Id_Encomienda] INT IDENTITY(1,1) PRIMARY KEY,
                    [CodigoSeguimiento] NVARCHAR(20) NOT NULL,
                    [NombreRemitente] NVARCHAR(100) NOT NULL,
                    [DniRemitente] NVARCHAR(15),
                    [TelefonoRemitente] NVARCHAR(15),
                    [NombreDestinatario] NVARCHAR(100) NOT NULL,
                    [DniDestinatario] NVARCHAR(15),
                    [TelefonoDestinatario] NVARCHAR(15),
                    [Descripcion] NVARCHAR(200) NOT NULL,
                    [PesoKg] DECIMAL(8,2),
                    [PrecioEnvio] DECIMAL(10,2) NOT NULL,
                    [Id_Ruta] INT,
                    [Estado] NVARCHAR(20) NOT NULL DEFAULT 'Registrado',
                    [Observaciones] NVARCHAR(300),
                    [FechaRegistro] DATETIME2 NOT NULL DEFAULT GETDATE(),
                    [FechaEntrega] DATETIME2,
                    [TurnoSalida] NVARCHAR(50),
                    [NombreConductor] NVARCHAR(100),
                    [RucRemitente] NVARCHAR(15),
                    [RucDestinatario] NVARCHAR(15),
                    [ClaveEnvio] NVARCHAR(20),
                    [MetodoPago] NVARCHAR(30) DEFAULT 'Efectivo',
                    [Id_Usuario] INT
                );

                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ProformasViajes' AND xtype='U')
                CREATE TABLE [ProformasViajes] (
                    [Id_Proforma] INT IDENTITY(1,1) PRIMARY KEY,
                    [CodigoProforma] NVARCHAR(25) NOT NULL,
                    [NombreCliente] NVARCHAR(120) NOT NULL,
                    [TipoDocumento] NVARCHAR(10) NOT NULL DEFAULT 'DNI',
                    [NumeroDocumento] NVARCHAR(20),
                    [TelefonoCliente] NVARCHAR(20),
                    [Id_Ruta] INT,
                    [FechaViaje] DATETIME2 NOT NULL,
                    [HoraSalida] NVARCHAR(50),
                    [TipoServicio] NVARCHAR(60) NOT NULL,
                    [CantidadPasajeros] INT NOT NULL DEFAULT 15,
                    [PrecioUnitario] DECIMAL(10,2),
                    [PrecioTotal] DECIMAL(10,2) NOT NULL,
                    [ValidezDias] INT NOT NULL DEFAULT 5,
                    [Observaciones] NVARCHAR(500),
                    [Estado] NVARCHAR(20) NOT NULL DEFAULT 'Emitida',
                    [FechaEmision] DATETIME2 NOT NULL DEFAULT GETDATE(),
                    [Id_Usuario] INT
                );
            ");
        }
        catch (Exception exTable)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(exTable, "Aviso verificando/creando tablas y columnas de base de datos");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = app.Services.CreateScope();
                var bgContext = bgScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await GeneradorViajesService.AsegurarRutasOficialesAsync(bgContext);
                await GeneradorViajesService.AsegurarViajesParaFechaAsync(bgContext, DateTime.Today);
            }
            catch { }
        });
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error durante la inicializacion o conexion con la base de datos.");
    }
}

app.Run();
