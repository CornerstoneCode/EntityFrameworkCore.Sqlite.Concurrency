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

### 2. Configure in Program.cs

#### Single-threaded / request-scoped use (ASP.NET Core controllers, Razor Pages, Blazor Server)

One context is created per HTTP request through the DI scope. ASP.NET Core processes requests one thread at a time per scope, so sharing a context here is safe.

```csharp
builder.Services.AddConcurrentSqliteDbContext<BlogDbContext>("Data Source=blog.db");
```

Or with custom options:

```csharp
builder.Services.AddConcurrentSqliteDbContext<BlogDbContext>(
    "Data Source=blog.db",
    options =>
    {
        options.BusyTimeout = TimeSpan.FromSeconds(30);
        options.MaxRetryAttempts = 5;
    });
```

#### Concurrent use (background workers, Task.WhenAll, channels, hosted services)

A `DbContext` is **not thread-safe** — it must not be shared across concurrent operations. Use `IDbContextFactory<T>` instead. Each concurrent flow calls `CreateDbContext()` to get its own independent instance.

```csharp
builder.Services.AddConcurrentSqliteDbContextFactory<BlogDbContext>("Data Source=blog.db");
```

Then inject and use the factory:

```csharp
public class PostImportService
{
    private readonly IDbContextFactory<BlogDbContext> _factory;

    public PostImportService(IDbContextFactory<BlogDbContext> factory)
        => _factory = factory;

    public async Task ImportPostsAsync(IEnumerable<Post> posts, CancellationToken ct)
    {
        var tasks = posts.Select(async post =>
        {
            await using var db = _factory.CreateDbContext();
            db.Posts.Add(post);
            await db.SaveChangesAsync(ct);
        });

        await Task.WhenAll(tasks); // ✅ Each task has its own context — no EF thread-safety violation
    }
}
```

> **Note:** `Cache=Shared` in the connection string is incompatible with WAL mode and will throw an `ArgumentException` at startup. Use the default connection string format (`Data Source=blog.db`) — connection pooling is enabled automatically.

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
// ❌ WRONG — sharing one DbContext across concurrent tasks
//    EF Core will throw InvalidOperationException about concurrent usage,
//    and SQLite returns "database is locked" for simultaneous writers.
public class TaskProcessor
{
    private readonly AppDbContext _context; // shared — unsafe for concurrent use

    public async Task ProcessTasksConcurrently()
    {
        var tasks = Enumerable.Range(1, 10)
            .Select(i => ProcessSingleTaskAsync(i));

        await Task.WhenAll(tasks); // 💥 EF thread-safety violation + database locked
    }

    private async Task ProcessSingleTaskAsync(int taskId)
    {
        _context.TaskResults.Add(new TaskResult { TaskId = taskId });
        await _context.SaveChangesAsync(); // 💥 concurrent SaveChanges on one context
    }
}

// ✅ CORRECT — one context per concurrent flow via IDbContextFactory
//    Register with: builder.Services.AddConcurrentSqliteDbContextFactory<AppDbContext>("Data Source=app.db");
public class TaskProcessor
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public TaskProcessor(IDbContextFactory<AppDbContext> factory)
        => _factory = factory;

    public async Task ProcessTasksConcurrently()
    {
        var tasks = Enumerable.Range(1, 10)
            .Select(i => ProcessSingleTaskAsync(i));

        await Task.WhenAll(tasks); // ✅ All tasks complete successfully
    }

    private async Task ProcessSingleTaskAsync(int taskId)
    {
        var result = await PerformWorkAsync(taskId);

        // Each concurrent flow creates and disposes its own context.
        // ThreadSafeEFCore.SQLite serializes the actual writes at the SQLite level.
        await using var db = _factory.CreateDbContext();
        db.TaskResults.Add(new TaskResult
        {
            TaskId = taskId,
            Result = result,
            CompletedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(); // ✅ Thread-safe — no shared context, writes queued automatically
    }
}
```

## Factory Pattern (When Not Using Dependency Injection)

```csharp
// Create contexts manually when needed
var dbContext = ThreadSafeFactory.CreateContext<BlogDbContext>(
    "Data Source=blog.db");

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
| `BusyTimeout` | 30 seconds | Per-connection `PRAGMA busy_timeout`. First layer of busy handling; SQLite retries lock acquisition internally for up to this duration. |
| `MaxRetryAttempts` | 3 | Application-level retry attempts for `SQLITE_BUSY*` errors, with exponential backoff and jitter. |
| `CommandTimeout` | 300 seconds | EF Core SQL command timeout in seconds. |
| `WalAutoCheckpoint` | 1000 pages | WAL auto-checkpoint interval (`PRAGMA wal_autocheckpoint`). Each page is 4 096 bytes by default (~4 MB). Set to `0` to disable. |
| `SynchronousMode` | `Normal` | Durability vs. performance trade-off (`PRAGMA synchronous`). `Normal` is recommended for WAL mode: safe against application crashes; a power loss or OS crash may roll back the last commit(s) not yet checkpointed. Use `Full` or `Extra` for stronger durability guarantees. |
| `UpgradeTransactionsToImmediate` | `true` | Rewrites `BEGIN`/`BEGIN TRANSACTION` to `BEGIN IMMEDIATE` to prevent `SQLITE_BUSY_SNAPSHOT` mid-transaction. Disable only if you manage write transactions explicitly yourself. |

## Multi-Instance Deployments and Migration Locks

EF Core uses a `__EFMigrationsLock` table to serialize concurrent migrations. If a migration process crashes after acquiring the lock but before releasing it, subsequent calls to `Database.Migrate()` will block indefinitely.

**Recommended approach:** run migrations once as a controlled startup step rather than calling `Database.Migrate()` from every app instance simultaneously.

If a stale lock does occur, use the built-in helper to detect and clear it:

```csharp
// In your startup or migration runner:
using var db = factory.CreateDbContext();
var connection = db.Database.GetDbConnection();
await connection.OpenAsync();

var wasStale = await SqliteConnectionEnhancer.TryReleaseMigrationLockAsync(connection);
if (wasStale)
    logger.LogWarning("Stale EF migration lock found and released. Proceeding with migration.");

await db.Database.MigrateAsync();
```

Pass `release: false` to check for a stale lock without removing it (useful for diagnostics).

> **Network filesystem warning:** SQLite WAL mode requires all connections to be on the **same physical host**. Do not point the database at an NFS, SMB, or other network-mounted path. If your app runs across multiple machines or containers, use a client/server database instead.

## Best Practices

1. **Use `IDbContextFactory<T>` for concurrent workloads** — inject the factory and call `CreateDbContext()` per concurrent operation; never share a single `DbContext` instance across concurrent tasks
2. **Use `AddConcurrentSqliteDbContext<T>` for request-scoped workloads** — standard ASP.NET Core controllers and Razor Pages where one request = one thread = one context
3. **Keep write transactions short** — acquire the write slot, write, commit; long-held write transactions block all other writers
4. **Use `BulkInsertOptimizedAsync`** for importing large amounts of data
5. **WAL mode is enabled automatically** — do not add `Cache=Shared` to the connection string; it is incompatible with WAL
6. **Run migrations from a single process** — avoid calling `Database.Migrate()` concurrently from multiple instances; use `TryReleaseMigrationLockAsync` if a stale lock occurs
7. **Stay on local disk** — WAL mode does not work over network filesystems (NFS, SMB); use a client/server database for multi-host deployments

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