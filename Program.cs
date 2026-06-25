using daluandou.Pages;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using daluandou.Data;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(@"/home/keys"))
    .SetApplicationName("daluandou");

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

string sqlServerConn = builder.Configuration.GetConnectionString("DefaultConnection")!;
string mySqlConn = builder.Configuration.GetConnectionString("MySqlConnection")!;
bool useSqlServer = TestSqlServerConnection(sqlServerConn);

if (useSqlServer)
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(sqlServerConn));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(mySqlConn, ServerVersion.AutoDetect(mySqlConn),
            b =>b.SchemaBehavior(MySqlSchemaBehavior.Ignore)));
}

builder.Services.AddSignalR();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Register");
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        //options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ChatHub>("/chatHub");
app.MapHub<GameRoomHub>("/gameHub");
app.MapRazorPages();

app.Run();

bool TestSqlServerConnection(string connString)
{
    try
    {
        using var conn = new SqlConnection(connString);
        conn.Open();
        return true;
    }
    catch
    {
        return false;
    }
}