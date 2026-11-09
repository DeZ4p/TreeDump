using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace TreeDump
{
    public class Project
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Session
    {
        [Key]
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    public class SentFile
    {
        [Key]
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string Path { get; set; } = string.Empty;
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }

    public class AppSetting
    {
        [Key]
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class AppDbContext : DbContext
    {
        private static readonly object SchemaLock = new();
        private static bool _schemaInitialized;

        public DbSet<Project> Projects => Set<Project>();
        public DbSet<Session> Sessions => Set<Session>();
        public DbSet<SentFile> SentFiles => Set<SentFile>();
        public DbSet<AppSetting> Settings => Set<AppSetting>();

        public static string DatabasePath { get; } = ResolveDatabasePath();

        public AppDbContext()
        {
            EnsureSchemaInitialized();
        }

        private static string ResolveDatabasePath()
        {
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "treedump.db");
            try
            {
                string? dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    return localPath;
                }
            }
            catch
            {
                // Fallback to LocalApplicationData if base directory is not accessible
            }

            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TreeDump");
            Directory.CreateDirectory(appDataDir);
            return Path.Combine(appDataDir, "treedump.db");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlite($"Data Source={DatabasePath};Pooling=True");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppSetting>().HasKey(s => s.Key);
            modelBuilder.Entity<Project>().HasIndex(p => p.RootPath);
            modelBuilder.Entity<Session>().HasIndex(s => s.ProjectId);
            modelBuilder.Entity<SentFile>().HasIndex(sf => sf.SessionId);
        }

        private void EnsureSchemaInitialized()
        {
            if (_schemaInitialized)
                return;

            lock (SchemaLock)
            {
                if (_schemaInitialized)
                    return;

                try
                {
                    Database.EnsureCreated();

                    Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                    Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
                    Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");

                    Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""Settings"" (
                            ""Key"" TEXT NOT NULL CONSTRAINT ""PK_Settings"" PRIMARY KEY,
                            ""Value"" TEXT NOT NULL
                        );");

                    Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""Projects"" (
                            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Projects"" PRIMARY KEY AUTOINCREMENT,
                            ""Name"" TEXT NOT NULL,
                            ""RootPath"" TEXT NOT NULL,
                            ""CreatedAt"" TEXT NOT NULL DEFAULT ''
                        );");

                    Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""Sessions"" (
                            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Sessions"" PRIMARY KEY AUTOINCREMENT,
                            ""ProjectId"" INTEGER NOT NULL,
                            ""StartedAt"" TEXT NOT NULL DEFAULT ''
                        );");

                    Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""SentFiles"" (
                            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SentFiles"" PRIMARY KEY AUTOINCREMENT,
                            ""SessionId"" INTEGER NOT NULL,
                            ""Path"" TEXT NOT NULL,
                            ""SentAt"" TEXT NOT NULL DEFAULT ''
                        );");

                    _schemaInitialized = true;
                }
                catch (Exception ex)
                {
                    App.LogException("EnsureSchemaInitialized", ex);
                }
            }
        }

        public string GetSetting(string key, string defaultValue = "")
        {
            if (string.IsNullOrWhiteSpace(key))
                return defaultValue;

            try
            {
                var entry = Settings.AsNoTracking().FirstOrDefault(s => s.Key == key);
                return entry != null && entry.Value != null ? entry.Value : defaultValue;
            }
            catch (Exception ex)
            {
                App.LogException($"GetSetting({key})", ex);
                return defaultValue;
            }
        }

        public void SetSetting(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            try
            {
                var entry = Settings.FirstOrDefault(s => s.Key == key);
                if (entry == null)
                {
                    Settings.Add(new AppSetting { Key = key, Value = value ?? string.Empty });
                }
                else
                {
                    entry.Value = value ?? string.Empty;
                }

                SaveChanges();
            }
            catch (Exception ex)
            {
                App.LogException($"SetSetting({key})", ex);
            }
        }

        public void ClearHistoryPreservingSettings()
        {
            using var tx = Database.BeginTransaction();
            try
            {
                Database.ExecuteSqlRaw(@"DELETE FROM ""SentFiles"";");
                Database.ExecuteSqlRaw(@"DELETE FROM ""Sessions"";");
                Database.ExecuteSqlRaw(@"DELETE FROM ""Projects"";");
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}