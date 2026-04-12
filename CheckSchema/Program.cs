using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace CheckSchema
{
    class Program
    {
        static void Main(string[] args)
        {
            try {
                string connectionString = "Server=sql8012.site4now.net;Database=db_ac6f45_mintuan;User Id=db_ac6f45_mintuan_admin;Password=Lumi@2026!;TrustServerCertificate=True;MultipleActiveResultSets=true;Connect Timeout=60;";
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    Console.WriteLine("--- SignalRConnections ---");
                    DataTable schema = connection.GetSchema("Columns", new[] { null, null, "SignalRConnections", null });
                    foreach (DataRow row in schema.Rows)
                    {
                        Console.WriteLine($"{row["COLUMN_NAME"]} - {row["DATA_TYPE"]} - IsNullable: {row["IS_NULLABLE"]}");
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
