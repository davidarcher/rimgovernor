namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// The controller-clock journal's tables. Native evidence (replies, epochs, observation contexts, statuses, event
    /// pages) is stored as protobuf blobs; the intent and the sequence, review and checkpoint bookkeeping are typed
    /// columns. Go's per-column CHECK constraints are dropped: the validation lives in <see cref="ClockJournal"/>.
    /// Singleton rows are created with the tables, as Go's initializers did.
    /// </summary>
    internal static readonly SchemaPart ClockPart = new(
        """
        CREATE TABLE clock_sequence(singleton INTEGER PRIMARY KEY CHECK(singleton=1), last_allocated TEXT NOT NULL, retired_through TEXT NOT NULL, retained TEXT NOT NULL) STRICT;
        INSERT INTO clock_sequence(singleton,last_allocated,retired_through,retained) VALUES(1,'0','0','');
        CREATE TABLE clock_attempts(request_id TEXT PRIMARY KEY, native_action_id TEXT NOT NULL UNIQUE, phase TEXT NOT NULL, reply BLOB, scope_context BLOB, intent_key TEXT NOT NULL, colony TEXT NOT NULL, map_id INTEGER NOT NULL, load_token TEXT NOT NULL, plan_id TEXT NOT NULL, revision TEXT NOT NULL, native_generation TEXT NOT NULL, kind TEXT NOT NULL, speed INTEGER NOT NULL, lease_ms INTEGER NOT NULL, max_ticks INTEGER NOT NULL, policy BLOB NOT NULL, original BLOB NOT NULL, test_acceleration INTEGER NOT NULL, blind_tick_budget INTEGER NOT NULL, max_ticks_per_second INTEGER NOT NULL, ceiling_set INTEGER NOT NULL, player_accelerated INTEGER NOT NULL, window_profile TEXT, window_tick INTEGER, window_review_revision TEXT, window_captured_cursor INTEGER, window_max_ticks INTEGER) STRICT;
        CREATE TABLE clock_epochs(start_request_id TEXT PRIMARY KEY REFERENCES clock_attempts(request_id), stage TEXT NOT NULL, sequence TEXT NOT NULL, context BLOB, status BLOB) STRICT;
        CREATE TABLE clock_inbox(singleton INTEGER PRIMARY KEY CHECK(singleton=1), profile TEXT NOT NULL) STRICT;
        CREATE TABLE clock_event_pages(sequence INTEGER PRIMARY KEY, after_cursor INTEGER NOT NULL UNIQUE, next_cursor INTEGER NOT NULL UNIQUE, request BLOB NOT NULL, page BLOB NOT NULL) STRICT;
        CREATE TABLE clock_inbox_events(cursor INTEGER PRIMARY KEY, page_sequence INTEGER NOT NULL REFERENCES clock_event_pages(sequence), payload BLOB NOT NULL) STRICT;
        CREATE TABLE clock_review(singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision TEXT NOT NULL, reviewed INTEGER NOT NULL, acknowledged INTEGER NOT NULL) STRICT;
        INSERT INTO clock_review(singleton,revision,reviewed,acknowledged) VALUES(1,'0',0,0);
        CREATE TABLE clock_review_log(sequence INTEGER PRIMARY KEY, kind TEXT NOT NULL, inbox_cursor INTEGER NOT NULL, expected_revision TEXT NOT NULL, through_cursor INTEGER NOT NULL, request_id TEXT NOT NULL) STRICT;
        CREATE TABLE clock_history_checkpoint(singleton INTEGER PRIMARY KEY CHECK(singleton=1), pages INTEGER NOT NULL, cursor INTEGER NOT NULL, lost_count TEXT NOT NULL, review_revision TEXT NOT NULL, review_reviewed INTEGER NOT NULL, review_acknowledged INTEGER NOT NULL, review_inbox_cursor INTEGER NOT NULL) STRICT;
        INSERT INTO clock_history_checkpoint(singleton,pages,cursor,lost_count,review_revision,review_reviewed,review_acknowledged,review_inbox_cursor) VALUES(1,0,0,'0','0',0,0,0);
        CREATE TABLE clock_acknowledgements(request_id TEXT PRIMARY KEY, expected_revision TEXT NOT NULL, through_cursor INTEGER NOT NULL, head_revision TEXT NOT NULL, head_reviewed INTEGER NOT NULL, head_acknowledged INTEGER NOT NULL, inbox_cursor INTEGER NOT NULL) STRICT;
        """,
        [
            "SELECT singleton,last_allocated,retired_through,retained FROM clock_sequence LIMIT 0",
            "SELECT request_id,native_action_id,phase,reply,scope_context,intent_key,colony,map_id,load_token,plan_id,revision,native_generation,kind,speed,lease_ms,max_ticks,policy,original,test_acceleration,blind_tick_budget,max_ticks_per_second,ceiling_set,player_accelerated,window_profile,window_tick,window_review_revision,window_captured_cursor,window_max_ticks FROM clock_attempts LIMIT 0",
            "SELECT start_request_id,stage,sequence,context,status FROM clock_epochs LIMIT 0",
            "SELECT singleton,profile FROM clock_inbox LIMIT 0",
            "SELECT sequence,after_cursor,next_cursor,request,page FROM clock_event_pages LIMIT 0",
            "SELECT cursor,page_sequence,payload FROM clock_inbox_events LIMIT 0",
            "SELECT singleton,revision,reviewed,acknowledged FROM clock_review LIMIT 0",
            "SELECT sequence,kind,inbox_cursor,expected_revision,through_cursor,request_id FROM clock_review_log LIMIT 0",
            "SELECT singleton,pages,cursor,lost_count,review_revision,review_reviewed,review_acknowledged,review_inbox_cursor FROM clock_history_checkpoint LIMIT 0",
            "SELECT request_id,expected_revision,through_cursor,head_revision,head_reviewed,head_acknowledged,inbox_cursor FROM clock_acknowledgements LIMIT 0",
        ]
    );
}
