// This type was promoted into production as
// src/AI.Investment.Infrastructure/Normalization/UsEquitySessionCalendar.cs, which the price
// normaliser now calls. Keeping a second copy here would be two sources of truth for the one
// rule that decides whether a stored timestamp is honest, so the copy is gone and
// UsEquitySessionTests exercises the production type directly.
//
// This file is empty on purpose and can be deleted; the bridge this repository is edited
// through cannot remove files, only write them.
