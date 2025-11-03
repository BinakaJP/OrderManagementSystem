using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderApi.Core.Entities;
using OrderApi.Data;
using Prometheus;

namespace OrderApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderDbContext _context;
    private readonly ILogger<OrdersController> _logger;
    
    // Custom Prometheus metrics
    private static readonly Counter OrdersCreated = Metrics
        .CreateCounter("orders_created_total", "Total number of orders created");
    
    private static readonly Gauge ActiveOrders = Metrics
        .CreateGauge("orders_active_total", "Current number of active orders");

    public OrdersController(OrderDbContext context, ILogger<OrdersController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Order>>> GetOrders(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 10)
    {
        var orders = await _context.Orders
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(orders);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
        {
            _logger.LogWarning("Order {OrderId} not found", id);
            return NotFound();
        }

        return Ok(order);
    }

    [HttpPost]
    public async Task<ActionResult<Order>> CreateOrder(CreateOrderRequest request)
    {
        var order = new Order
        {
            CustomerId = request.CustomerId,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            Items = request.Items.Select(item => new OrderItem
            {
                ProductName = item.ProductName,
                Quantity = item.Quantity,
                Price = item.Price
            }).ToList()
        };

        order.TotalAmount = order.Items.Sum(i => i.Price * i.Quantity);

        _context.Orders.Add(order);
        await _context.SaveChangesAsync();

        // Increment Prometheus counter
        OrdersCreated.Inc();
        
        _logger.LogInformation("Created order {OrderId} for customer {CustomerId}", 
            order.Id, order.CustomerId);

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        var order = await _context.Orders.FindAsync(id);
        if (order == null)
        {
            return NotFound();
        }

        order.Status = request.Status;
        order.UpdatedAt = DateTime.UtcNow;
        
        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated order {OrderId} status to {Status}", id, request.Status);

        return NoContent();
    }

    [HttpGet("stats")]
    public async Task<ActionResult<OrderStats>> GetStats()
    {
        var totalOrders = await _context.Orders.CountAsync();
        var ordersByStatus = await _context.Orders
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var totalRevenue = await _context.Orders
            .Where(o => o.Status != OrderStatus.Cancelled)
            .SumAsync(o => o.TotalAmount);

        // Update active orders gauge
        var activeCount = await _context.Orders
            .CountAsync(o => o.Status != OrderStatus.Delivered && o.Status != OrderStatus.Cancelled);
        ActiveOrders.Set(activeCount);

        return Ok(new OrderStats
        {
            TotalOrders = totalOrders,
            OrdersByStatus = ordersByStatus.ToDictionary(x => x.Status.ToString(), x => x.Count),
            TotalRevenue = totalRevenue
        });
    }
}

// DTOs
public record CreateOrderRequest(
    string CustomerId,
    List<CreateOrderItemRequest> Items
);

public record CreateOrderItemRequest(
    string ProductName,
    int Quantity,
    decimal Price
);

public record UpdateStatusRequest(OrderStatus Status);

public record OrderStats
{
    public int TotalOrders { get; init; }
    public Dictionary<string, int> OrdersByStatus { get; init; } = new();
    public decimal TotalRevenue { get; init; }
}