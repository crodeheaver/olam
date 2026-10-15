using System.Text.Json.Serialization;

namespace Olam.Tests;

[Flags]
public enum ProductStatus { Inactive = 0, Active = 1, Featured = 2 }

public sealed class Product
{
    public int Id { get; set; }
    [JsonPropertyName("display_name")]
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public double Rating { get; set; }
    public int? Stock { get; set; }
    public bool Active { get; set; }
    public ProductStatus Status { get; set; }
    public Guid Sku { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateOnly IntroducedOn { get; set; }
    public TimeOnly OpeningTime { get; set; }
    public DateTime UpdatedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public Supplier Supplier { get; set; } = new();
    public List<Order> Orders { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}

public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Address Address { get; set; } = new();
}

public sealed class Address
{
    public string City { get; set; } = "";
}

public sealed class Order
{
    public int Id { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ProductView
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CategorySummary
{
    public string Category { get; set; } = "";
    public decimal Total { get; set; }
    public int Count { get; set; }
}
