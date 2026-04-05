using System;
using Microsoft.Data.SqlClient;
using System.IO;

namespace SqlPatch
{
    class Program
    {
        static void Main(string[] args)
        {
            string connectionString = "Server=sql8012.site4now.net;Database=db_ac6f45_mintuan;User Id=db_ac6f45_mintuan_admin;Password=Lumi@2026!;TrustServerCertificate=True;";
            string scriptPath = @"D:\DACK\patch.sql";
            string script = File.ReadAllText(scriptPath);

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                Console.WriteLine("Connected to remote DB.");
                
                using (SqlCommand command = new SqlCommand(script, connection))
                {
                    try
                    {
                        command.ExecuteNonQuery();
                        Console.WriteLine("Patch successfully executed.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error: " + ex.Message);
                    }
                }
            }
        }
    }
}
