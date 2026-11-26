//! Undo history as snapshots of the timeline: each step keeps the timeline as it was before the edit, so every
//! edit undoes the same way and no edit needs its own undo code.

use fig_timeline::Timeline;

/// How an edit that continues the one before it (a slider drag) joins that step.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Coalesce {
    /// The edit carries the whole change from where the run began (a slider's value, a drag's total distance):
    /// it is applied to the timeline as it was before the run.
    Restart,
    /// The edit is a further change (a drag's latest step): it is applied to the timeline as it is now.
    Continue,
}

struct Step {
    before: Timeline,
    key: Option<String>,
}

pub struct History {
    undo: Vec<Step>,
    redo: Vec<Timeline>,
    max_depth: usize,
    group_depth: usize,
    group_start: usize,
    sealed: bool,
}

impl History {
    pub fn new(max_depth: usize) -> History {
        History { undo: Vec::new(), redo: Vec::new(), max_depth, group_depth: 0, group_start: 0, sealed: false }
    }

    pub fn max_depth(&self) -> usize {
        self.max_depth
    }

    pub fn can_undo(&self) -> bool {
        !self.undo.is_empty()
    }

    pub fn can_redo(&self) -> bool {
        !self.redo.is_empty()
    }

    /// Records an edit already made: `before` is how the timeline was, `doc` how it is. Nothing is recorded when
    /// nothing changed.
    pub fn record(&mut self, before: Timeline, doc: &Timeline, key: Option<String>) -> bool {
        self.sealed = false;
        if before == *doc {
            return false;
        }
        self.undo.push(Step { before, key });
        self.redo.clear();
        if self.undo.len() > self.max_depth {
            let excess = self.undo.len() - self.max_depth;
            self.undo.drain(..excess);
            self.group_start = self.group_start.saturating_sub(excess);
        }
        true
    }

    /// The timeline from before the run that an edit with `key` would join, if it would join one.
    pub fn coalescing_base(&self, key: &str) -> Option<&Timeline> {
        let floor = if self.group_depth > 0 { self.group_start } else { 0 };
        if self.sealed || self.undo.len() <= floor {
            return None;
        }
        let top = self.undo.last()?;
        (top.key.as_deref() == Some(key)).then_some(&top.before)
    }

    /// Notes that an edit joined the step on top (no new step), so redo is gone.
    pub fn joined(&mut self) {
        self.redo.clear();
    }

    /// Ends the current coalescing run, so the next edit is its own undo step (each gizmo drag is one).
    pub fn break_coalescing(&mut self) {
        self.sealed = true;
    }

    /// Starts a group: everything recorded until the matching [`end_group`](Self::end_group) becomes one undo
    /// step. Groups nest; the outermost one makes the step.
    pub fn begin_group(&mut self) {
        if self.group_depth == 0 {
            self.group_start = self.undo.len();
        }
        self.group_depth += 1;
    }

    pub fn end_group(&mut self) {
        debug_assert!(self.group_depth > 0);
        self.group_depth = self.group_depth.saturating_sub(1);
        if self.group_depth > 0 {
            return;
        }
        if self.undo.len() > self.group_start + 1 {
            // the first step's "before" is the whole group's
            let mut steps = self.undo.drain(self.group_start..);
            let first = steps.next().unwrap();
            drop(steps);
            self.undo.push(Step { before: first.before, key: None });
        }
    }

    /// Steps back: puts the timeline as it was before the last edit in `doc`, keeping the current one for redo.
    pub fn undo(&mut self, doc: &mut Timeline) -> bool {
        let Some(step) = self.undo.pop() else { return false };
        self.redo.push(std::mem::replace(doc, step.before));
        true
    }

    pub fn redo(&mut self, doc: &mut Timeline) -> bool {
        let Some(after) = self.redo.pop() else { return false };
        let before = std::mem::replace(doc, after);
        self.undo.push(Step { before, key: None });
        true
    }

    /// Forgets every step.
    pub fn clear(&mut self) {
        self.undo.clear();
        self.redo.clear();
    }
}
