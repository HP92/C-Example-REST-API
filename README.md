# MongoRedisApi

A learning-oriented .NET 8 REST API that demonstrates how to build a backend application in C#. It persists data in **MongoDB**, caches responses in **Redis**, runs everything in **Docker**, and has **unit tests** with xUnit and Moq.

> **Goal:** Show how the core pieces of a real-world C# backend fit together — from an HTTP request arriving at the server all the way to the database and back.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [How the Pieces Connect](#how-the-pieces-connect)
   - [The Request Lifecycle](#the-request-lifecycle)
   - [Dependency Injection](#dependency-injection--the-glue-of-aspnet-core)
   - [Interfaces and Why They Matter](#interfaces-and-why-they-matter)
   - [Async / Await](#async--await)
   - [The Cache-Aside Pattern](#the-cache-aside-pattern)
   - [Docker Compose Orchestration](#docker-compose-orchestration)
3. [Project Structure](#project-structure)
4. [File-by-File Guide](#file-by-file-guide)
5. [Getting Started](#getting-started)
6. [API Endpoints](#api-endpoints)
7. [Configuration](#configuration)
8. [Running the Tests](#running-the-tests)
9. [License](#license)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                  Docker Compose                      │
│                                                      │
│  ┌──────────────────┐       ┌──────────────────┐    │
│  │   MongoRedisApi  │──────▶│   MongoDB :27017  │    │
│  │   ASP.NET Core   │       └──────────────────┘    │
│  │   :8080          │       ┌──────────────────┐    │
│  │                  │◀─────▶│   Redis   :6379   │    │
│  └──────────────────┘       └──────────────────┘    │
└─────────────────────────────────────────────────────┘
          ▲
          │  HTTP requests
          ▼
     HTTP Client
  (browser / Postman)
```

The API container waits for both MongoDB and Redis to pass their health checks before it starts, so there are no "connection refused" errors on cold boot.

---

## How the Pieces Connect

### The Request Lifecycle

Every HTTP request travels through a fixed chain of responsibility:

```
HTTP Request
    │
    ▼
┌─────────────────────────┐
│  ASP.NET Core Middleware │  (routing, JSON serialization, Swagger)
└────────────┬────────────┘
             │
             ▼
┌─────────────────────────┐
│   ProductsController    │  Receives the request, validates input,
│   (Controllers/)        │  calls the service, returns HTTP response.
└────────────┬────────────┘
             │  calls IProductService
             ▼
┌─────────────────────────┐
│   ProductService        │  All business logic lives here.
│   (Services/)           │  Decides whether to use the cache or the DB.
└──────┬──────────────────┘
       │                  │
       ▼                  ▼
┌──────────┐        ┌──────────┐
│  Redis   │        │ MongoDB  │
│ (cache)  │        │  (store) │
└──────────┘        └──────────┘
```

The controller knows **nothing** about MongoDB or Redis. The service knows **nothing** about HTTP. This separation makes each layer easier to understand, test, and replace.

---

### Dependency Injection — the glue of ASP.NET Core

`Program.cs` is the application's entry point and configuration hub. Instead of classes creating their own dependencies with `new`, ASP.NET Core's **DI container** creates them and injects them where needed.

```csharp
// Register once:
builder.Services.AddSingleton<IMongoClient>(...);   // one instance for the whole app
builder.Services.AddScoped<IProductService, ProductService>(); // one per HTTP request

// ASP.NET Core automatically injects these into ProductsController:
public ProductsController(IProductService service)  // ← injected by the container
```

**Why this matters:**
- You never need to manually pass dependencies around.
- Swapping `ProductService` for a different implementation (e.g. a fake for tests) requires changing **one line** in `Program.cs`.
- Lifetimes (`Singleton`, `Scoped`, `Transient`) control how long an object lives.

---

### Interfaces and Why They Matter

`IProductService` (in `Services/`) defines a *contract* — what operations the service must support — without saying *how* they work.

```csharp
public interface IProductService
{
    Task<List<Product>> GetAllAsync();
    Task<Product?> GetByIdAsync(string id);
    // ...
}
```

`ProductsController` depends on `IProductService`, not on `ProductService`. This means:

1. **Tests** can inject a `Mock<IProductService>` — no real database needed.
2. The controller compiles and runs even if `ProductService` is replaced entirely.
3. It makes the dependency direction explicit and readable.

The same principle is used for `IMongoClient` and `IDistributedCache` from the MongoDB and Redis libraries — both are interfaces, which is why the unit tests can mock them.

---

### Async / Await

Almost every method in this project returns `Task<T>` and uses `await`:

```csharp
// Synchronous (blocks the thread while waiting for the DB):
var product = _collection.FindOne(filter);  // ❌ thread is frozen

// Asynchronous (the thread is free to handle other requests while waiting):
var product = await _collection.FindAsync(filter);  // ✅
```

In a web server, threads are a finite resource. `async/await` lets one thread serve many requests concurrently without blocking. This is especially important for I/O-bound work like database and cache calls.

---

### The Cache-Aside Pattern

Every read follows this flow:

```
GET /api/products/{id}
        │
        ▼
 Check Redis cache ──── HIT ──▶ Return cached JSON (fast, ~1 ms)
        │
       MISS
        │
        ▼
 Query MongoDB ──────────────▶ Return document
        │
        ▼
 Write result to Redis cache   (next request will be a HIT)
        │
        ▼
 Return response
```

Every write (`POST`, `PUT`, `DELETE`) immediately **removes** the affected cache keys, so stale data is never served.

Cache keys used:

| Key              | Contains                    | Invalidated by       |
|------------------|-----------------------------|----------------------|
| `products:all`   | All products as JSON array  | POST, PUT, DELETE    |
| `products:{id}`  | Single product as JSON      | PUT /{id}, DELETE /{id} |

Cache TTL: **5 minutes** absolute, **2-minute** sliding window.

---

### Docker Compose Orchestration

`docker-compose.yml` defines three services and their relationships:

```yaml
services:
  mongo:   # MongoDB database — data persisted in a named volume
  redis:   # Redis cache     — data persisted in a named volume
  api:     # The .NET API    — depends_on mongo + redis (waits for healthchecks)
```

The `api` service uses `depends_on` with `condition: service_healthy`, meaning Docker will not start the API container until both `mongo` and `redis` report healthy. This prevents the "connection refused on startup" problem.

The `Dockerfile` uses a **multi-stage build**:
1. **Build stage** — uses the full .NET SDK image to compile the app.
2. **Runtime stage** — copies only the compiled output into a smaller ASP.NET runtime image.

The final image is much smaller because the SDK (compilers, build tools) is not included.

---

## Project Structure

```
c#/
├── MongoRedisApi.sln              ← solution file (groups both projects)
├── docker-compose.yml             ← starts MongoDB, Redis, and the API
├── .dockerignore
├── README.md
├── LICENSE
│
├── MongoRedisApi/                 ← main application
│   ├── Dockerfile
│   ├── MongoRedisApi.csproj       ← project file (NuGet dependencies, SDK target)
│   ├── Program.cs                 ← entry point; DI wiring; middleware pipeline
│   ├── appsettings.json           ← default configuration
│   ├── appsettings.Development.json
│   ├── Configuration/
│   │   └── MongoDbSettings.cs     ← typed config class (avoids magic strings)
│   ├── Controllers/
│   │   └── ProductsController.cs  ← HTTP layer: routes, status codes, [FromBody]
│   ├── Models/
│   │   └── Product.cs             ← data shape; BSON attributes for MongoDB mapping
│   └── Services/
│       ├── IProductService.cs     ← contract (interface)
│       └── ProductService.cs      ← implementation: MongoDB queries + Redis caching
│
└── MongoRedisApi.Tests/           ← unit test project
    ├── MongoRedisApi.Tests.csproj
    └── Services/
        └── ProductServiceTests.cs ← 10 tests covering all CRUD paths + cache logic
```

---

## File-by-File Guide

| File | What it does |
|------|-------------|
| `Program.cs` | Configures DI, registers MongoDB client (singleton), Redis cache, and the service. Adds Swagger. |
| `Configuration/MongoDbSettings.cs` | A plain C# class whose properties map 1-to-1 to the `MongoDbSettings` section in `appsettings.json`. Using a typed class instead of raw `IConfiguration` gives compile-time safety. |
| `Models/Product.cs` | Defines the shape of a product document. `[BsonId]` marks the MongoDB `_id` field; `[BsonRepresentation]` controls how the ObjectId is serialized (as a string in JSON, as an ObjectId in BSON). |
| `Services/IProductService.cs` | The interface (contract). The controller depends on this, not on the concrete class. |
| `Services/ProductService.cs` | Implements the cache-aside logic. Talks to MongoDB via `IMongoCollection<Product>` and to Redis via `IDistributedCache`. |
| `Controllers/ProductsController.cs` | Maps HTTP verbs and routes to service calls. Returns appropriate status codes (`200 OK`, `201 Created`, `204 No Content`, `404 Not Found`). |
| `appsettings.json` | Default settings (local dev). In Docker, these are overridden by environment variables in `docker-compose.yml`. |
| `Dockerfile` | Multi-stage build: SDK image for compilation, ASP.NET runtime image for the final container. |
| `docker-compose.yml` | Declares all three services, their ports, volumes, health checks, and startup order. |
| `MongoRedisApi.Tests/Services/ProductServiceTests.cs` | 10 xUnit tests. MongoDB and Redis are replaced with Moq mocks so tests run without any infrastructure. |

---

## Getting Started

### Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (includes Docker Compose v2)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) *(only needed to run locally or run tests)*

### Run with Docker Compose

```bash
# From the c# folder:
docker compose up --build
```

| Service | URL |
|---------|-----|
| API | http://localhost:8080 |
| Swagger UI | http://localhost:8080/swagger |
| MongoDB | localhost:27017 |
| Redis | localhost:6379 |

### Run locally (without Docker)

Ensure MongoDB and Redis are running locally, then:

```bash
cd MongoRedisApi
dotnet run
```

---

## API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/products` | List all products *(served from cache when available)* |
| `GET` | `/api/products/{id}` | Get a single product *(served from cache when available)* |
| `POST` | `/api/products` | Create a product *(invalidates list cache)* |
| `PUT` | `/api/products/{id}` | Replace a product *(invalidates item + list cache)* |
| `DELETE` | `/api/products/{id}` | Delete a product *(invalidates item + list cache)* |

### Example request body (`POST` / `PUT`)

```json
{
  "name": "Wireless Keyboard",
  "description": "Compact mechanical keyboard",
  "price": 89.99,
  "category": "Electronics",
  "stock": 150
}
```

---

## Configuration

Settings are read from `appsettings.json`. Environment variables override them using `__` as the section separator (used in `docker-compose.yml`).

| Setting | Default | Env variable override |
|---------|---------|----------------------|
| MongoDB connection | `mongodb://localhost:27017` | `MongoDbSettings__ConnectionString` |
| Database name | `ProductsDb` | `MongoDbSettings__DatabaseName` |
| Collection name | `Products` | `MongoDbSettings__CollectionName` |
| Redis connection | `localhost:6379` | `ConnectionStrings__Redis` |

---

## Running the Tests

The test project uses **xUnit** as the test framework and **Moq** to replace MongoDB and Redis with in-memory fakes. No running containers are needed.

```bash
# Run all tests from the solution root:
dotnet test MongoRedisApi.sln

# Or from the test project folder:
cd MongoRedisApi.Tests
dotnet test
```

### What is tested

| Test | Scenario |
|------|----------|
| `GetAllAsync_CacheMiss_*` | Cache is empty → MongoDB is queried → result is written to cache |
| `GetAllAsync_CacheHit_*` | Cache has data → MongoDB is **not** queried |
| `GetByIdAsync_CacheMiss_*` | Cache empty, product found → MongoDB queried, cached |
| `GetByIdAsync_CacheHit_*` | Cache has product → MongoDB **not** queried |
| `GetByIdAsync_NotFound_*` | Product doesn't exist → `null` returned, cache **not** written |
| `CreateAsync_*` | Product inserted into MongoDB, list cache invalidated |
| `UpdateAsync_WhenFound_*` | Product replaced, item + list cache invalidated |
| `UpdateAsync_WhenNotFound_*` | `false` returned, cache **not** touched |
| `DeleteAsync_WhenFound_*` | Product deleted, item + list cache invalidated |
| `DeleteAsync_WhenNotFound_*` | `false` returned, cache **not** touched |

---

## License

This project is licensed under the [MIT License](LICENSE).
