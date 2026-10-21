using Microsoft.EntityFrameworkCore;
using SistemaChotaExpress.Models;

namespace SistemaChotaExpress.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<Roles> Roles { get; set; }
        public DbSet<Usuario> Usuarios { get; set; }
        public DbSet<Bus> Buses { get; set; }
        public DbSet<Ruta> Rutas { get; set; }
        public DbSet<Viaje> Viajes { get; set; }
        public DbSet<Pasajero> Pasajeros { get; set; }
        public DbSet<Venta> Ventas { get; set; }
        public DbSet<Encomienda> Encomiendas { get; set; }
        public DbSet<ProformaViaje> ProformasViajes { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SistemaChotaExpress_BD;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True");
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Roles>().HasData(
                new Roles { Id_Rol = 1, NombreRol = "Gerente" },
                new Roles { Id_Rol = 2, NombreRol = "Vendedor" }
            );

            modelBuilder.Entity<Bus>().HasData(
                new Bus { Id_Bus = 1, Placa = "S/P", NombreVehiculo = "Combi 01", NombreConductor = "Juan Pérez", Modelo = "Toyota HiAce (Combi)", Capacidad = 15 },
                new Bus { Id_Bus = 2, Placa = "S/P", NombreVehiculo = "Combi 02", NombreConductor = "Carlos Gómez", Modelo = "Hyundai H350", Capacidad = 15 },
                new Bus { Id_Bus = 3, Placa = "S/P", NombreVehiculo = "Combi 03", NombreConductor = "Mario Silva", Modelo = "Toyota HiAce (Combi)", Capacidad = 15 },
                new Bus { Id_Bus = 4, Placa = "S/P", NombreVehiculo = "Combi 04", NombreConductor = "Luis Fernández", Modelo = "Mercedes-Benz Sprinter", Capacidad = 15 }
            );

            modelBuilder.Entity<Ruta>().HasData(
                new Ruta { Id_Ruta = 1, Origen = "Chiclayo", Destino = "Chota", DuracionHoras = 6.0 },
                new Ruta { Id_Ruta = 2, Origen = "Chota", Destino = "Chiclayo", DuracionHoras = 6.0 },
                new Ruta { Id_Ruta = 3, Origen = "Cajamarca", Destino = "Chota", DuracionHoras = 4.0 },
                new Ruta { Id_Ruta = 4, Origen = "Chota", Destino = "Cajamarca", DuracionHoras = 4.0 },
                new Ruta { Id_Ruta = 5, Origen = "Cajamarca", Destino = "Chiclayo", DuracionHoras = 7.0 },
                new Ruta { Id_Ruta = 6, Origen = "Chiclayo", Destino = "Cajamarca", DuracionHoras = 7.0 },
                new Ruta { Id_Ruta = 7, Origen = "Bambamarca", Destino = "Chota", DuracionHoras = 1.5 },
                new Ruta { Id_Ruta = 8, Origen = "Chota", Destino = "Bambamarca", DuracionHoras = 1.5 },
                new Ruta { Id_Ruta = 9, Origen = "Chota", Destino = "Cutervo", DuracionHoras = 2.0 },
                new Ruta { Id_Ruta = 10, Origen = "Cutervo", Destino = "Chota", DuracionHoras = 2.0 }
            );

            modelBuilder.Entity<Usuario>().HasData(
                new Usuario
                {
                    Id_Usuario = 1,
                    Name = "Gerente General",
                    Email = "gerente@chotaexpress.com",
                    Password = "gerente123",
                    Telefono = "976543210",
                    Id_Rol = 1
                },
                new Usuario
                {
                    Id_Usuario = 2,
                    Name = "Juan Vendedor",
                    Email = "vendedor@chotaexpress.com",
                    Password = "vendedor123",
                    Telefono = "987654321",
                    Id_Rol = 2
                }
            );

            modelBuilder.Entity<Venta>()
                .HasOne(v => v.ObjetoViaje)
                .WithMany(v => v.Ventas)
                .HasForeignKey(v => v.Id_Viaje)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Venta>()
                .HasOne(v => v.ObjetoPasajero)
                .WithMany()
                .HasForeignKey(v => v.Id_Pasajero)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Venta>()
                .HasOne(v => v.ObjetoUsuario)
                .WithMany()
                .HasForeignKey(v => v.Id_Usuario)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Pasajero>()
                .HasIndex(p => new { p.TipoDocumento, p.NumeroDocumento })
                .IsUnique();

            modelBuilder.Entity<Viaje>()
                .HasOne(v => v.ObjetoRuta)
                .WithMany()
                .HasForeignKey(v => v.Id_Ruta)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
