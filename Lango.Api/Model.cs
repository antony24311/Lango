namespace Lango.Api;

public class Word
{
    public long Id { get; set; }
    public string Lemma { get; set; } = "";
    public string? Pos { get; set; }
    public string? DefsJson { get; set; }
    public string? PhoneticsJson { get; set; }

    
    public string[]? Synonyms { get; set; }
    public string[]? Antonyms { get; set; }
    public string[]? Collocations { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Lookup
{
    public long Id { get; set; }
    public string UserId { get; set; } = ""; // ���Φr��A����i���� Auth
    public long WordId { get; set; }
    public string? Source { get; set; }   // iOS-share / web-ext / etc.
    public string? Sentence { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Review
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public long WordId { get; set; }
    public DateTime DueDate { get; set; }
    public int? LastGrade { get; set; }        // 0..5
    public int NextIntervalDays { get; set; }  // �Ѻt��k���@
}

public class Sentence
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Raw { get; set; } = "";
    public string? Corrected { get; set; }
    public string? Explainer { get; set; } // ��k�I����
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Quiz
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTime ForDate { get; set; }
    public string PayloadJson { get; set; } = "{}"; // �D�ؤ��e�]JSON�^
    public bool Done { get; set; }
}
