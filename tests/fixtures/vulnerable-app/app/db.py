"""INTENTIONALLY INSECURE FIXTURE — see ../README.md.

Textbook SQL injection: the user-supplied value is concatenated straight into the query
string instead of being passed as a parameter. Semgrep's SAST rules flag this.
"""
import sqlite3


def find_user(connection: sqlite3.Connection, username: str):
    cursor = connection.cursor()
    # BAD: string concatenation of untrusted input into SQL.
    query = "SELECT * FROM users WHERE name = '" + username + "'"
    cursor.execute(query)
    return cursor.fetchall()
