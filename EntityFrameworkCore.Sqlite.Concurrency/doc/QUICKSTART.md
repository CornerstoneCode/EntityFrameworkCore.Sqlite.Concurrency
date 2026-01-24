# ThreadSafeEFCore.SQLite

**Eliminate SQLite "database is locked" errors with simple, thread-safe database operations.**

## The Problem It Solves

If you've ever used SQLite with Entity Framework Core in a multi-threaded application, you've probably encountered the dreaded `Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'`. SQLite only allows one writer at a time, causing failures when multiple threads try to write simultaneously.

**ThreadSafeEFCore.SQLite** solves this completely. It automatically queues write operations while allowing unlimited parallel reads.

## Installation

```bash
dotnet add package ThreadSafeEFCore.SQLite
```

## Quick Start

### 1. Create Your DbContext Normally

```csharp
public class BlogDbContext : DbContext
{
    public DbSet<Post> Posts { get; set; }
    public DbSet<Comment> Comments { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your normal configuration
        modelBuilder.Entity<Post>()
            .HasIndex(p => p.Slug)
            .IsUnique();
    }
}
```

### 2. Configure with One Line of Code

In your `Program.cs` or startup configuration:

```csharp
// Simple configuration
builder.Services.AddDbContext<BlogDbContext>(options =>
    options.UseSqliteWithConcurrency("Data Source=blog.db"));
```

Or with custom options:

```csharp
builder.Services.AddDbContext<BlogDbContext>(options =>
    options.UseSqliteWithConcurrency(
        "Data Source=blog.db",
        sqliteOptions =>
        {
            sqliteOptions.UseWriteQueue = true;     // Enable write serialization
            sqliteOptions.BusyTimeout = TimeSpan.FromSeconds(30);
            sqliteOptions.MaxRetryAttempts = 5;
        }));
```

## Basic Usage Examples

### Writing Data (Automatically Thread-Safe)

```csharp
public class PostService
{
    private readonly BlogDbContext _context;
    
    public PostService(BlogDbContext context)
    {
        _context = context;
    }
    
    public async Task CreatePostAsync(string title, string content)
    {
        // Write operations are automatically serialized
        // No need to worry about locks or concurrency issues
        var post = new Post 
        { 
            Title = title, 
            Content = content,
            Slug = GenerateSlug(title),
            CreatedAt = DateTime.UtcNow
        };
        
        _context.Posts.Add(post);
        await _context.SaveChangesAsync(); // Thread-safe!
    }
    
    public async Task AddCommentAsync(int postId, string author, string text)
    {
        var comment = new Comment
        {
            PostId = postId,
            Author = author,
            Text = text,
            PostedAt = DateTime.UtcNow
        };
        
        // Multiple threads can call this simultaneously
        // Writes are queued automatically
        _context.Comments.Add(comment);
        await _context.SaveChangesAsync();
    }
}
```

### Reading Data (Fully Parallel)

```csharp
public class PostService
{
    // ... constructor and other methods ...
    
    public async Task<PostSummary> GetPostSummaryAsync(int postId)
    {
        // Reads execute in parallel - no blocking!
        var postTask = _context.Posts
            .FirstOrDefaultAsync(p => p.Id == postId);
            
        var commentsTask = _context.Comments
            .Where(c => c.PostId == postId)
            .OrderByDescending(c => c.PostedAt)
            .Take(10)
            .ToListAsync();
            
        var countTask = _context.Comments
            .CountAsync(c => c.PostId == postId);
        
        // All reads execute simultaneously
        await Task.WhenAll(postTask, commentsTask, countTask);
        
        return new PostSummary
        {
            Post = await postTask,
            RecentComments = await commentsTask,
            TotalComments = await countTask
        };
    }
}
```

### Bulk Operations Made Easy

```csharp
public class ImportService
{
    private readonly BlogDbContext _context;
    
    public async Task ImportPostsAsync(List<Post> posts)
    {
        // Bulk insert with automatic concurrency handling
        await _context.BulkInsertOptimizedAsync(posts);
        
        // Or use the retry wrapper for extra safety
        await _context.ExecuteWithRetryAsync(async ctx =>
        {
            // Complex import logic
            await ProcessAndSavePostsAsync(ctx, posts);
        });
    }
}
```

## Real-World Scenario: Background Processing

Imagine a scenario where multiple background workers are processing tasks:

```csharp
// WITHOUT ThreadSafeEFCore.SQLite - This would fail with "database is locked"
public class TaskProcessor
{
    public async Task ProcessTasksConcurrently()
    {
        var tasks = Enumerable.Range(1, 10)
            .Select(i => ProcessSingleTaskAsync(i));
            
        await Task.WhenAll(tasks); // 💥 Database locked errors!
    }
}

// WITH ThreadSafeEFCore.SQLite - Just works!
public class TaskProcessor
{
    private readonly AppDbContext _context;
    
    public async Task ProcessTasksConcurrently()
    {
        var tasks = Enumerable.Range(1, 10)
            .Select(i => ProcessSingleTaskAsync(i));
            
        await Task.WhenAll(tasks); // ✅ All tasks complete successfully
    }
    
    private async Task ProcessSingleTaskAsync(int taskId)
    {
        // Each task writes to the database
        var result = await PerformWorkAsync(taskId);
        
        // The package automatically queues these writes
        _context.TaskResults.Add(new TaskResult
        {
            TaskId = taskId,
            Result = result,
            CompletedAt = DateTime.UtcNow
        });
        
        await _context.SaveChangesAsync();
    }
}
```

## Factory Pattern (When Not Using Dependency Injection)

```csharp
// Create contexts manually when needed
var dbContext = ThreadSafeFactory.CreateContext<BlogDbContext>(
    "Data Source=blog.db",
    options => options.UseWriteQueue = true);

// Use it
await dbContext.Posts.AddAsync(new Post { Title = "Hello World" });
await dbContext.SaveChangesAsync();
```

## Error Handling and Retries

The package includes built-in retry logic, but you can add your own:

```csharp
public async Task UpdatePostWithRetryAsync(int postId, string newContent)
{
    try
    {
        await _context.ExecuteWithRetryAsync(async ctx =>
        {
            var post = await ctx.Posts.FindAsync(postId);
            post.Content = newContent;
            post.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }, maxRetries: 5);
    }
    catch (Exception ex)
    {
        // Handle persistent failures
        _logger.LogError(ex, "Failed to update post {PostId}", postId);
        throw;
    }
}
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `UseWriteQueue` | `true` | Automatically queue write operations |
| `BusyTimeout` | 30 seconds | How long to wait if database is busy |
| `MaxRetryAttempts` | 3 | Number of retries for busy errors |
| `CommandTimeout` | 300 seconds | SQL command timeout |
| `EnableWalCheckpointManagement` | `true` | Automatically manage WAL checkpoints |

## Best Practices

1. **Use Dependency Injection** when possible for automatic context management
2. **Keep write transactions short** - queue your data and write quickly
3. **Use `BulkInsertOptimizedAsync`** for importing large amounts of data
4. **Enable WAL mode** (already done by default) for better concurrency
5. **Monitor performance** with the built-in diagnostics when needed

## What Makes It Different

| Traditional EF Core + SQLite | ThreadSafeEFCore.SQLite |
|------------------------------|-------------------------|
| ❌ `database is locked` errors | ✅ Automatic write queuing |
| ❌ Manual retry logic needed | ✅ Built-in exponential backoff |
| ❌ Read blocking during writes | ✅ True parallel reads |
| ❌ Complex synchronization code | ✅ Simple, intuitive API |

## Summary

**ThreadSafeEFCore.SQLite** lets you write multi-threaded applications as if SQLite had full concurrent write support. Just change your `UseSqlite()` call to `UseSqliteWithConcurrency()` and forget about database locks forever.

```csharp
// Before: Constant locking issues
options.UseSqlite("Data Source=app.db");

// After: Thread-safe by default
options.UseSqliteWithConcurrency("Data Source=app.db");
```

Write your application logic, not concurrency workarounds.