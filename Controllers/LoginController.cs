using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SistemaChotaExpress.Data;
using SistemaChotaExpress.Models;
using SistemaChotaExpress.ViewModels;

namespace SistemaChotaExpress.Controllers
{
    public class LoginController : Controller
    {
        private readonly AppDbContext _context;

        public LoginController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToDashboard();
            return View();
        }

        // ACCIÓN 1: Inicio de sesión exclusivo para el Gerente Fijo
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoginGerente(string Email, string Password)
        {
            var emailClean = Email?.Trim().ToLower();
            if (emailClean == "gerente@chotaexpress.com" && Password == "gerente123")
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "Gerente General"),
                    new Claim(ClaimTypes.Email, "gerente@chotaexpress.com"),
                    new Claim(ClaimTypes.Role, "Gerente")
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                return RedirectToAction("Dashboard", "Gerente");
            }

            ModelState.AddModelError(string.Empty, "Credenciales de Gerente incorrectas.");
            TempData["ErrorGerente"] = "Correo o contraseña de Gerente incorrectos.";
            return View("Login");
        }

        // ACCIÓN 2: Inicio de sesión para los Vendedores desde la Base de Datos
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoginVendedor(LoginVM modelo)
        {
            if (ModelState.IsValid)
            {
                var emailClean = modelo.Email?.Trim().ToLower();
                var passwordClean = modelo.Password;

                // Si el gerente ingresa por este formulario, también se le permite el acceso
                if (emailClean == "gerente@chotaexpress.com" && passwordClean == "gerente123")
                {
                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, "Gerente General"),
                        new Claim(ClaimTypes.Email, "gerente@chotaexpress.com"),
                        new Claim(ClaimTypes.Role, "Gerente")
                    };

                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                    return RedirectToAction("Dashboard", "Gerente");
                }

                var usuario = await _context.Usuarios
                    .Include(u => u.ObjetoRol)
                    .FirstOrDefaultAsync(u => u.Email == emailClean && u.Password == passwordClean);

                if (usuario != null)
                {
                    var roleName = usuario.ObjetoRol?.NombreRol ?? (usuario.Id_Rol == 1 ? "Gerente" : "Vendedor");
                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, usuario.Name),
                        new Claim(ClaimTypes.Email, usuario.Email),
                        new Claim(ClaimTypes.Role, roleName)
                    };

                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                    if (roleName == "Gerente")
                    {
                        TempData["BienvenidaLogin"] = usuario.Name;
                        return RedirectToAction("Dashboard", "Gerente");
                    }
                    TempData["BienvenidaLogin"] = usuario.Name;
                    return RedirectToAction("Inicio", "Venta");
                }

                ModelState.AddModelError(string.Empty, "Correo o contraseña de vendedor incorrectos.");
                TempData["ErrorVendedor"] = "Correo o contraseña de vendedor incorrectos.";
            }
            return View("Login", modelo);
        }

        public async Task<IActionResult> Salir()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();
            return RedirectToAction("Login");
        }

        [HttpPost]
        [HttpGet]
        public async Task<IActionResult> Logout() => await Salir();

        [HttpGet]
        public IActionResult AccesoDenegado()
        {
            return View();
        }

        // Redirige al dashboard según el rol del usuario autenticado
        private IActionResult RedirectToDashboard()
        {
            if (User.IsInRole("Gerente"))
            {
                return RedirectToAction("Dashboard", "Gerente");
            }
            return RedirectToAction("Inicio", "Venta");
        }
    }
}
