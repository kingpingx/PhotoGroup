namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Migrations;

internal static class SqlScripts
{
    /// <summary>
    /// The initial schema.
    /// </summary>
    /// <remarks>
    /// Ids are BLOB(16) holding a UUIDv7 in RFC 9562 big-endian order, so that index
    /// locality follows creation time. Timestamps are ISO-8601 strings in TEXT, which sort
    /// correctly and survive inspection with a SQL browser, at the cost of some space.
    ///
    /// The whole designed schema is created at once rather than one table per milestone.
    /// The shape is settled, and a single starting point is easier to reason about than a
    /// chain of migrations that only ever ran in development.
    ///
    /// Path columns collate NOCASE because Windows resolves paths case-insensitively: without
    /// it, the same file reached by two spellings would be indexed as two photos. Note that
    /// SQLite's NOCASE folds ASCII only, so paths differing solely in the case of a non-ASCII
    /// character are still treated as distinct.
    /// </remarks>
    public const string V1Initial = """
        CREATE TABLE scan_roots (
            id           BLOB    NOT NULL PRIMARY KEY,
            path         TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            recursive    INTEGER NOT NULL DEFAULT 1,
            is_implicit  INTEGER NOT NULL DEFAULT 0,
            last_scan_utc TEXT
        );

        CREATE TABLE photos (
            id            BLOB    NOT NULL PRIMARY KEY,
            path          TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            file_size     INTEGER NOT NULL,
            mtime_utc     TEXT    NOT NULL,
            content_hash  TEXT,
            width         INTEGER,
            height        INTEGER,
            orientation   INTEGER NOT NULL DEFAULT 1,
            taken_utc     TEXT,
            camera        TEXT,
            state         INTEGER NOT NULL DEFAULT 0,
            indexed_utc   TEXT,
            error         TEXT
        );

        CREATE INDEX ix_photos_state ON photos (state);
        CREATE INDEX ix_photos_content_hash ON photos (content_hash) WHERE content_hash IS NOT NULL;
        CREATE INDEX ix_photos_taken ON photos (taken_utc);

        CREATE TABLE persons (
            id            BLOB NOT NULL PRIMARY KEY,
            display_name  TEXT NOT NULL,
            cover_face_id BLOB,
            centroid      BLOB,
            created_utc   TEXT NOT NULL
        );

        CREATE UNIQUE INDEX ux_persons_name ON persons (display_name COLLATE NOCASE);

        CREATE TABLE clusters (
            id             BLOB    NOT NULL PRIMARY KEY,
            detector_id    TEXT    NOT NULL,
            embedder_id    TEXT    NOT NULL,
            person_id      BLOB    REFERENCES persons (id) ON DELETE SET NULL,
            size           INTEGER NOT NULL DEFAULT 0,
            medoid_face_id BLOB,
            created_utc    TEXT    NOT NULL
        );

        CREATE INDEX ix_clusters_pair ON clusters (detector_id, embedder_id);

        -- A face belongs to one detector's view of a photo. Both detectors' faces coexist
        -- so that switching detectors is reversible: the old set is deactivated, not
        -- deleted, and switching back restores the person assignments immediately.
        CREATE TABLE faces (
            id               BLOB    NOT NULL PRIMARY KEY,
            photo_id         BLOB    NOT NULL REFERENCES photos (id) ON DELETE CASCADE,
            detector_id      TEXT    NOT NULL,
            detector_version TEXT    NOT NULL,
            active           INTEGER NOT NULL DEFAULT 1,
            bbox_x           REAL    NOT NULL,
            bbox_y           REAL    NOT NULL,
            bbox_w           REAL    NOT NULL,
            bbox_h           REAL    NOT NULL,
            det_score        REAL    NOT NULL,
            landmarks        BLOB    NOT NULL,
            blur_score       REAL,
            face_px          INTEGER NOT NULL,
            cluster_id       BLOB    REFERENCES clusters (id) ON DELETE SET NULL,
            person_id        BLOB    REFERENCES persons (id) ON DELETE SET NULL,
            assignment       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX ix_faces_photo ON faces (photo_id, detector_id);
        CREATE INDEX ix_faces_person ON faces (person_id) WHERE active = 1;
        CREATE INDEX ix_faces_cluster ON faces (cluster_id);
        CREATE INDEX ix_faces_active ON faces (detector_id, active);

        -- Vectors live apart from faces: they are large, dimensionality varies by embedder,
        -- and vectors from different embedders are not comparable, so the embedder has to
        -- be part of the key. Keeping them out of the faces row also keeps the many UI
        -- queries that never read a vector fast.
        CREATE TABLE face_embeddings (
            face_id          BLOB    NOT NULL REFERENCES faces (id) ON DELETE CASCADE,
            embedder_id      TEXT    NOT NULL,
            embedder_version TEXT    NOT NULL,
            dim              INTEGER NOT NULL,
            vector           BLOB    NOT NULL,
            PRIMARY KEY (face_id, embedder_id)
        );

        -- Review decisions. Persisted because no algorithm can regenerate them: losing
        -- these means the user redoes the review by hand.
        CREATE TABLE face_links (
            face_a      BLOB    NOT NULL REFERENCES faces (id) ON DELETE CASCADE,
            face_b      BLOB    NOT NULL REFERENCES faces (id) ON DELETE CASCADE,
            kind        INTEGER NOT NULL,
            created_utc TEXT    NOT NULL,
            PRIMARY KEY (face_a, face_b),
            -- Enforcing the ordering makes a duplicate pair in reverse order impossible to
            -- write, which is cheaper than de-duplicating on read forever after.
            CHECK (face_a < face_b)
        );

        CREATE INDEX ix_face_links_b ON face_links (face_b);

        CREATE TABLE export_runs (
            id            BLOB    NOT NULL PRIMARY KEY,
            started_utc   TEXT    NOT NULL,
            finished_utc  TEXT,
            output_root   TEXT    NOT NULL,
            pattern       TEXT    NOT NULL,
            mode          INTEGER NOT NULL,
            source        INTEGER NOT NULL,
            status        INTEGER NOT NULL,
            undone_utc    TEXT
        );

        -- Doubles as the undo journal for move runs, which is why it is persisted rather
        -- than held in memory for the duration of the run: a crash mid-move is exactly
        -- when the record of what moved where matters most.
        CREATE TABLE export_ops (
            id         BLOB    NOT NULL PRIMARY KEY,
            run_id     BLOB    NOT NULL REFERENCES export_runs (id) ON DELETE CASCADE,
            photo_id   BLOB    NOT NULL REFERENCES photos (id) ON DELETE CASCADE,
            person_id  BLOB    REFERENCES persons (id) ON DELETE SET NULL,
            src_path   TEXT    NOT NULL,
            dst_path   TEXT    NOT NULL,
            op         INTEGER NOT NULL,
            status     INTEGER NOT NULL,
            bytes      INTEGER NOT NULL DEFAULT 0,
            src_hash   TEXT,
            error      TEXT
        );

        CREATE INDEX ix_export_ops_run ON export_ops (run_id, status);

        CREATE TABLE settings (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Records which detector has already examined which photograph.
    /// </summary>
    /// <remarks>
    /// Added because detection progress was being tracked on the photo itself, as a single state
    /// column, while detection is inherently per-detector. Once a photograph had been examined by
    /// one detector it was marked done for all of them, so choosing the other detector and asking
    /// for detection silently found nothing to do.
    ///
    /// A count is stored rather than inferring completion from the presence of faces, because
    /// "examined and found nobody" and "not yet examined" are different states that look identical
    /// in the faces table. Without the distinction, every photograph containing no people would be
    /// re-examined on every run, forever.
    /// </remarks>
    public const string V2PhotoDetections = """
        CREATE TABLE photo_detections (
            photo_id         BLOB    NOT NULL REFERENCES photos (id) ON DELETE CASCADE,
            detector_id      TEXT    NOT NULL,
            detector_version TEXT    NOT NULL,
            face_count       INTEGER NOT NULL,
            detected_utc     TEXT    NOT NULL,
            PRIMARY KEY (photo_id, detector_id)
        );

        CREATE INDEX ix_photo_detections_detector ON photo_detections (detector_id);

        -- Reconstructed for photographs that already have faces, so that upgrading does not
        -- discard detection work that has already been paid for. Photographs examined and found
        -- to contain nobody cannot be recovered this way and will be examined once more.
        INSERT INTO photo_detections (photo_id, detector_id, detector_version, face_count, detected_utc)
        SELECT photo_id, detector_id, MIN(detector_version), COUNT(*), '1970-01-01T00:00:00.0000000+00:00'
        FROM faces
        GROUP BY photo_id, detector_id;
        """;

    /// <summary>
    /// Faces the user has said they do not care about.
    /// </summary>
    /// <remarks>
    /// A photo library is full of strangers: people in the background, on posters, walking past.
    /// Left in, they form groups that sit on the People screen forever asking to be named, and
    /// there is no answer that makes them go away.
    ///
    /// Recorded per face rather than per group, because groups are rebuilt from scratch on every
    /// run and carry no identity between them. Marking a group would be forgotten the next time
    /// grouping was pressed, and the same strangers would come straight back.
    /// </remarks>
    public const string V3IgnoredFaces = """
        CREATE TABLE ignored_faces (
            face_id     BLOB NOT NULL PRIMARY KEY REFERENCES faces (id) ON DELETE CASCADE,
            created_utc TEXT NOT NULL
        );
        """;

    /// <summary>
    /// What a photograph looks like, for finding near-duplicates.
    /// </summary>
    /// <remarks>
    /// A table of its own rather than columns on photos, for the same reason face embeddings are
    /// not columns on faces: it is derived, it is recomputed as a batch, and a photo row is read
    /// on every screen in the application while this is read by one. Cascading on delete keeps it
    /// from outliving the photograph it describes.
    ///
    /// The fingerprint is a pair of INTEGERs because comparison is by how many bits differ, not by
    /// equality, and because one direction is not enough to tell pictures apart. Stored as signed
    /// values, which is how SQLite holds a 64-bit integer; the top bit is part of the fingerprint
    /// and the adapter reinterprets rather than losing it.
    /// </remarks>
    public const string V4PhotoSignatures = """
        CREATE TABLE photo_signatures (
            photo_id     BLOB    NOT NULL PRIMARY KEY REFERENCES photos (id) ON DELETE CASCADE,
            hash_across  INTEGER NOT NULL,
            hash_down    INTEGER NOT NULL,
            sharpness    REAL    NOT NULL,
            computed_utc TEXT    NOT NULL
        );
        """;
}
