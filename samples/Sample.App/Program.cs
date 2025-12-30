// Program.cs
// This demonstrates using a Blake-generated class

using Sample.Examples;
using Sample.Models;

var user = new User
{
    Id = 1,
    FirstName = "Blake",
    LastName = "Verigin",
    Email = "blake@example.com",
    Age = 0,
    IsActive = true
};

Console.WriteLine(user);
Console.WriteLine($"Total fields: {User.FieldCount}");

var clone = user.Clone();
clone.Id = 2;
Console.WriteLine($"Cloned: {clone}");

Console.WriteLine($"User role: {user.Role}");

var demo = new VarReturnTypeDemo();
Console.WriteLine($"GetName: {demo.GetName()}");
Console.WriteLine($"GetVersion: {demo.GetVersion()}");
Console.WriteLine($"GetGreeting: {demo.GetGreeting(formal: true)}");
Console.WriteLine($"FetchDataAsync: {await demo.FetchDataAsync()}");
Console.WriteLine($"Add/Subtract/Multiply: {demo.Subtract(demo.Multiply(demo.Add(1, 2), 3), 4)}");
demo.LogCalculatedArea(5);
