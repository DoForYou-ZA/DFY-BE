using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/testimonials")]
public class TestimonialsController : ControllerBase
{
    private readonly AppDbContext _context;
    public TestimonialsController(AppDbContext context) => _context = context;

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> GetPublished()
    {
        var items = await _context.Testimonials.AsNoTracking().Where(t => t.IsPublished)
            .OrderBy(t => t.SortOrder).ThenByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Name, t.Role, t.Text, t.SortOrder }).ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = items });
    }

    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> GetAll()
    {
        var items = await _context.Testimonials.AsNoTracking().OrderBy(t => t.SortOrder)
            .ThenByDescending(t => t.CreatedAt).ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = items });
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> Create([FromBody] TestimonialRequest request)
    {
        var validation = Validate(request);
        if (validation != null) return BadRequest(new ApiResponse<object> { Success = false, Message = validation });
        var nextOrder = await _context.Testimonials.MaxAsync(t => (int?)t.SortOrder) ?? 0;
        var now = DateTime.UtcNow;
        var item = new Testimonial { Name = request.Name.Trim(), Role = request.Role.Trim(), Text = request.Text.Trim(), IsPublished = request.IsPublished, SortOrder = request.SortOrder ?? nextOrder + 1, CreatedAt = now, UpdatedAt = now };
        _context.Testimonials.Add(item);
        await _context.SaveChangesAsync();
        await AuditAsync("CreateTestimonial", item, null);
        return Ok(new ApiResponse<object> { Success = true, Data = item, Message = "Testimonial created" });
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> Update(int id, [FromBody] TestimonialRequest request)
    {
        var validation = Validate(request);
        if (validation != null) return BadRequest(new ApiResponse<object> { Success = false, Message = validation });
        var item = await _context.Testimonials.FindAsync(id);
        if (item == null) return NotFound(new ApiResponse<object> { Success = false, Message = "Testimonial not found" });
        var old = JsonSerializer.Serialize(new { item.Name, item.Role, item.Text, item.IsPublished, item.SortOrder });
        item.Name = request.Name.Trim(); item.Role = request.Role.Trim(); item.Text = request.Text.Trim();
        item.IsPublished = request.IsPublished; if (request.SortOrder.HasValue) item.SortOrder = request.SortOrder.Value;
        item.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await AuditAsync("UpdateTestimonial", item, old);
        return Ok(new ApiResponse<object> { Success = true, Data = item, Message = "Testimonial updated" });
    }

    [HttpPatch("{id:int}/publish")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> SetPublished(int id, [FromBody] PublishTestimonialRequest request)
    {
        var item = await _context.Testimonials.FindAsync(id);
        if (item == null) return NotFound(new ApiResponse<object> { Success = false, Message = "Testimonial not found" });
        var old = item.IsPublished.ToString();
        item.IsPublished = request.IsPublished; item.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await AuditAsync(request.IsPublished ? "PublishTestimonial" : "UnpublishTestimonial", item, old);
        return Ok(new ApiResponse<object> { Success = true, Data = item, Message = request.IsPublished ? "Testimonial published" : "Testimonial unpublished" });
    }

    [HttpPatch("{id:int}/reorder")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> Reorder(int id, [FromBody] ReorderTestimonialRequest request)
    {
        var item = await _context.Testimonials.FindAsync(id);
        if (item == null) return NotFound(new ApiResponse<object> { Success = false, Message = "Testimonial not found" });
        item.SortOrder = request.SortOrder; item.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await AuditAsync("ReorderTestimonial", item, null);
        return Ok(new ApiResponse<object> { Success = true, Data = item });
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(int id)
    {
        var item = await _context.Testimonials.FindAsync(id);
        if (item == null) return NotFound(new ApiResponse<bool> { Success = false, Message = "Testimonial not found" });
        var old = JsonSerializer.Serialize(new { item.Name, item.Role, item.Text, item.IsPublished, item.SortOrder });
        _context.Testimonials.Remove(item);
        await _context.SaveChangesAsync();
        _context.AuditLogs.Add(new AuditLog { UserId = AdminId(), Action = "DeleteTestimonial", EntityType = "Testimonial", EntityId = id, OldValues = old, IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", UserAgent = Request.Headers.UserAgent.ToString() });
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Testimonial deleted" });
    }

    private static string? Validate(TestimonialRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100) return "Name is required and must be 100 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.Role) || request.Role.Trim().Length > 80) return "Role is required and must be 80 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > 1200) return "Testimonial text is required and must be 1,200 characters or fewer.";
        return null;
    }
    private int? AdminId() => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private async Task AuditAsync(string action, Testimonial item, string? oldValue)
    {
        _context.AuditLogs.Add(new AuditLog { UserId = AdminId(), Action = action, EntityType = "Testimonial", EntityId = item.Id, OldValues = oldValue, NewValues = JsonSerializer.Serialize(new { item.Name, item.Role, item.Text, item.IsPublished, item.SortOrder }), IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", UserAgent = Request.Headers.UserAgent.ToString() });
        await _context.SaveChangesAsync();
    }
}
public sealed class TestimonialRequest
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public int? SortOrder { get; set; }
}
public sealed class PublishTestimonialRequest { public bool IsPublished { get; set; } }
public sealed class ReorderTestimonialRequest { public int SortOrder { get; set; } }
