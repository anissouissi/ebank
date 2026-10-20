using Microsoft.EntityFrameworkCore;

namespace Ebank.Api.Data;

public sealed class EbankDbContext(DbContextOptions<EbankDbContext> options) : DbContext(options)
{
    public DbSet<KycProfile> KycProfiles => Set<KycProfile>();
    public DbSet<KycDocument> KycDocuments => Set<KycDocument>();
    public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
    public DbSet<AccountTransaction> AccountTransactions => Set<AccountTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KycProfile>(entity =>
        {
            entity.HasKey(profile => profile.Subject);
            entity.Property(profile => profile.Subject).HasMaxLength(200);
            entity.Property(profile => profile.FirstName).HasMaxLength(100);
            entity.Property(profile => profile.LastName).HasMaxLength(100);
            entity.Property(profile => profile.PhoneNumber).HasMaxLength(32);
            entity.Property(profile => profile.AddressLine1).HasMaxLength(200);
            entity.Property(profile => profile.AddressLine2).HasMaxLength(200);
            entity.Property(profile => profile.City).HasMaxLength(100);
            entity.Property(profile => profile.PostalCode).HasMaxLength(32);
            entity.Property(profile => profile.Country).HasMaxLength(2);
            entity.Property(profile => profile.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasMany(profile => profile.Documents)
                .WithOne(document => document.Profile)
                .HasForeignKey(document => document.Subject)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KycDocument>(entity =>
        {
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Subject).HasMaxLength(200);
            entity.Property(document => document.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(document => document.MediaType).HasMaxLength(64);
            entity.Property(document => document.EncryptedContent).HasColumnType("bytea");
            entity.Property(document => document.Nonce).HasColumnType("bytea");
            entity.Property(document => document.AuthenticationTag).HasColumnType("bytea");
            entity.HasIndex(document => new { document.Subject, document.Type }).IsUnique();
        });

        modelBuilder.Entity<CustomerAccount>(entity =>
        {
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Subject).HasMaxLength(200);
            entity.Property(account => account.AccountNumber).HasMaxLength(35);
            entity.Property(account => account.Type).HasConversion<string>().HasMaxLength(24);
            entity.Property(account => account.Currency).HasMaxLength(3).IsFixedLength();
            entity.Property(account => account.Balance).HasPrecision(18, 2);
            entity.HasIndex(account => account.AccountNumber).IsUnique();
            entity.HasIndex(account => new { account.Subject, account.CreatedAt });
            entity.HasMany(account => account.Transactions)
                .WithOne(transaction => transaction.Account)
                .HasForeignKey(transaction => transaction.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountTransaction>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.Type)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(TransactionType.Other);
            entity.Property(transaction => transaction.Label).HasMaxLength(160);
            entity.Property(transaction => transaction.Currency).HasMaxLength(3).IsFixedLength();
            entity.Property(transaction => transaction.Amount).HasPrecision(18, 2);
            entity.HasIndex(transaction => new { transaction.AccountId, transaction.OccurredAt });
        });
    }
}

public sealed class KycProfile
{
    public required string Subject { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public KycStatus Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<KycDocument> Documents { get; } = new List<KycDocument>();
}

public sealed class KycDocument
{
    public Guid Id { get; set; }
    public required string Subject { get; set; }
    public required KycDocumentType Type { get; set; }
    public required string MediaType { get; set; }
    public required byte[] EncryptedContent { get; set; }
    public required byte[] Nonce { get; set; }
    public required byte[] AuthenticationTag { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public KycProfile Profile { get; set; } = null!;
}

public enum KycDocumentType
{
    IdentityDocument,
    AddressProof
}

public enum KycStatus
{
    Incomplete,
    PendingReview,
    Approved,
    Rejected
}

public sealed class CustomerAccount
{
    public Guid Id { get; set; }
    public required string Subject { get; set; }
    public required string AccountNumber { get; set; }
    public required AccountType Type { get; set; }
    public required string Currency { get; set; }
    public decimal Balance { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<AccountTransaction> Transactions { get; } = new List<AccountTransaction>();
}

public sealed class AccountTransaction
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public TransactionType Type { get; set; }
    public required string Label { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public CustomerAccount Account { get; set; } = null!;
}

public enum TransactionType
{
    Other,
    EcommercePurchase,
    AtmWithdrawal,
    SalaryReceived,
    MoneyTransfer
}

/// <summary>Identifies the kind of customer demo account.</summary>
public enum AccountType
{
    Current,
    Savings
}
