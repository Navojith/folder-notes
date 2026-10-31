namespace FolderNotes.Core.Domain;

/// <summary>One broken validation rule. <see cref="Code"/> is stable (specs/01-requirements.md § Validation rules).</summary>
public sealed record ValidationError(string Code, string Field, string Message);

/// <summary>Thrown when saving a note or label that breaks a validation rule.</summary>
public sealed class NoteValidationException : Exception
{
    public NoteValidationException(IReadOnlyList<ValidationError> errors)
        : base("Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Code} ({e.Message})")))
    {
        Errors = errors;
    }

    public NoteValidationException()
        : this([])
    {
    }

    public NoteValidationException(string message)
        : base(message)
    {
        Errors = [];
    }

    public NoteValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    public IReadOnlyList<ValidationError> Errors { get; }
}

/// <summary>The REQ-002 / REQ-010 rules. Pure, so the editor can run it on every keystroke.</summary>
public static class NoteValidator
{
    public static IReadOnlyList<ValidationError> Validate(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var errors = new List<ValidationError>();

        var title = note.Title ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add(new("title.required", "title", "A title is required."));
        }
        else if (TextLength.Of(title) > Note.MaxTitleLength)
        {
            errors.Add(new("title.too-long", "title", $"The title can be at most {Note.MaxTitleLength} characters."));
        }

        if (title.Any(char.IsControl))
        {
            errors.Add(new("title.single-line", "title", "The title must be a single line."));
        }

        if (TextLength.Of(note.BodyMd ?? "") > Note.MaxBodyLength)
        {
            errors.Add(new("body.too-long", "body", $"The body can be at most {Note.MaxBodyLength:N0} characters."));
        }

        if (!note.Status.IsDefined())
        {
            errors.Add(new("status.invalid", "status", "Unknown status."));
        }

        if (note.AccentColor is not null && !Palette.IsValid(note.AccentColor))
        {
            errors.Add(new("accent.invalid", "accent", $"'{note.AccentColor}' isn't a palette color."));
        }

        var labels = note.Labels ?? [];
        foreach (var label in labels)
        {
            errors.AddRange(ValidateLabel(label));
        }

        var duplicates = labels
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .GroupBy(l => l.Name.Trim(), Label.NameComparer)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var name in duplicates)
        {
            errors.Add(new("labels.duplicate", "labels", $"The label '{name}' is added more than once."));
        }

        if (note.Folder is null || string.IsNullOrWhiteSpace(note.Folder.PathNorm) || string.IsNullOrWhiteSpace(note.Folder.PathDisplay))
        {
            errors.Add(new("folder.required", "folder", "The note has no folder."));
        }

        if (note.UpdatedAt < note.CreatedAt)
        {
            errors.Add(new("timestamps.order", "updated_at", "updated_at is earlier than created_at."));
        }

        return errors;
    }

    public static IReadOnlyList<ValidationError> ValidateLabel(Label label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var errors = new List<ValidationError>();
        var name = label.Name ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(new("label.name-required", "label.name", "A label name is required."));
        }
        else
        {
            if (TextLength.Of(name) > Label.MaxNameLength)
            {
                errors.Add(new("label.name-too-long", "label.name", $"A label name can be at most {Label.MaxNameLength} characters."));
            }

            if (name.Any(char.IsControl) || name.Contains('"', StringComparison.Ordinal) || name != name.Trim())
            {
                errors.Add(new("label.name-invalid", "label.name", $"'{name}' isn't a valid label name."));
            }
        }

        if (!Palette.IsValid(label.Color))
        {
            errors.Add(new("label.color-invalid", "label.color", $"'{label.Color}' isn't a palette color."));
        }

        return errors;
    }

    public static void EnsureValid(Note note)
    {
        var errors = Validate(note);
        if (errors.Count > 0)
        {
            throw new NoteValidationException(errors);
        }
    }

    public static void EnsureValid(Label label)
    {
        var errors = ValidateLabel(label);
        if (errors.Count > 0)
        {
            throw new NoteValidationException(errors);
        }
    }
}
