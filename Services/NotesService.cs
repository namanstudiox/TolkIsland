using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TolkIsland.Services;

public sealed class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public bool Done { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class NotesService
{
    private const int MaxNotes = 40;
    private readonly object _sync = new();
    private readonly List<NoteItem> _notes = new();
    private readonly string _path;
    private string _savesBlockedMessage = string.Empty;

    public NotesService()
    {
        _path = Path.Combine(SettingsService.DataDirectory, "notes.json");
        Load();
    }

    public event Action? Changed;

    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<NoteItem> Items
    {
        get
        {
            lock (_sync)
            {
                return _notes.Select(Clone).ToList();
            }
        }
    }

    public SaveResult Add(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return SaveResult.Success;
        }

        if (text.Length > 500)
        {
            text = text[..500];
        }

        SaveResult result;
        lock (_sync)
        {
            var snapshot = _notes.Select(Clone).ToList();

            _notes.Insert(0, new NoteItem { Text = text });
            if (_notes.Count > MaxNotes)
            {
                _notes.RemoveRange(MaxNotes, _notes.Count - MaxNotes);
            }

            result = SaveLocked();
            if (!result.Ok)
            {
                // Keep memory in sync with the file when the write fails.
                _notes.Clear();
                _notes.AddRange(snapshot);
            }

            LastError = result.Ok ? string.Empty : result.Message;
        }

        if (result.Ok)
        {
            Changed?.Invoke();
        }

        return result;
    }

    public SaveResult Toggle(string id)
    {
        SaveResult result = SaveResult.Success;

        lock (_sync)
        {
            var note = _notes.FirstOrDefault(item => item.Id == id);
            if (note is not null)
            {
                note.Done = !note.Done;
                result = SaveLocked();
                if (!result.Ok)
                {
                    note.Done = !note.Done;
                }

                LastError = result.Ok ? string.Empty : result.Message;
            }
        }

        if (result.Ok)
        {
            Changed?.Invoke();
        }

        return result;
    }

    public SaveResult Remove(string id)
    {
        SaveResult result = SaveResult.Success;
        var changed = false;

        lock (_sync)
        {
            var index = _notes.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                var note = _notes[index];
                _notes.RemoveAt(index);
                result = SaveLocked();
                if (!result.Ok)
                {
                    _notes.Insert(index, note);
                }

                LastError = result.Ok ? string.Empty : result.Message;
                changed = true;
            }
        }

        if (changed && result.Ok)
        {
            Changed?.Invoke();
        }

        return result;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var notes = JsonSerializer.Deserialize<List<NoteItem>>(json);
            if (notes is null)
            {
                return;
            }

            _notes.AddRange(notes
                .Where(note => note is not null && !string.IsNullOrWhiteSpace(note.Text))
                .Take(MaxNotes)
                .Select(Clone));
        }
        catch (Exception exception)
        {
            QuarantineUnreadableFile(exception);
        }
    }

    private void QuarantineUnreadableFile(Exception exception)
    {
        var message = exception is JsonException
            ? "The notes file could not be read."
            : "The notes file is unavailable.";

        try
        {
            var quarantine = Path.Combine(
                SettingsService.DataDirectory,
                $"notes.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_path, quarantine);
            LastError = $"{message} The old file was kept as {Path.GetFileName(quarantine)}. Starting with an empty list.";
        }
        catch
        {
            _savesBlockedMessage =
                $"{message} It could not be moved aside, so notes are read-only until the file is fixed or removed.";
            LastError = _savesBlockedMessage;
        }
    }

    private SaveResult SaveLocked()
    {
        if (!string.IsNullOrEmpty(_savesBlockedMessage))
        {
            return SaveResult.Fail(SaveFailure.Unknown, _savesBlockedMessage);
        }

        try
        {
            var json = JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true });
            return SettingsStorage.WriteAtomic(_path, json);
        }
        catch (Exception exception)
        {
            return SaveResult.From(exception);
        }
    }

    private static NoteItem Clone(NoteItem source) => new()
    {
        Id = source.Id,
        Text = source.Text,
        Done = source.Done,
        CreatedAt = source.CreatedAt
    };
}
